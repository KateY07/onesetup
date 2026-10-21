// onesetup-ui: a small native shell that runs install.bat and turns its ".command" output lines into UI.
//   onesetup-ui [--headless] [--auto-close] [--no-glass] <script.bat> [script arguments]
// Win32 + DirectComposition + Direct2D/DirectWrite, no third-party code. Exit code = script exit code (2 = cancelled).
#define WIN32_LEAN_AND_MEAN
#define NOMINMAX
#include <windows.h>
#include <windowsx.h>
#include <shellapi.h>
#include <dwmapi.h>
#include <d3d11.h>
#include <dxgi1_3.h>
#include <d2d1_1.h>
#include <dwrite.h>
#include <dcomp.h>
#include <wincodec.h>
#include <wrl/client.h>
#include <algorithm>
#include <atomic>
#include <cmath>
#include <deque>
#include <functional>
#include <string>
#include <thread>
#include <vector>

#pragma comment(lib, "user32.lib")
#pragma comment(lib, "gdi32.lib")
#pragma comment(lib, "shell32.lib")
#pragma comment(lib, "dwmapi.lib")
#pragma comment(lib, "d3d11.lib")
#pragma comment(lib, "dxgi.lib")
#pragma comment(lib, "d2d1.lib")
#pragma comment(lib, "dwrite.lib")
#pragma comment(lib, "dcomp.lib")

using Microsoft::WRL::ComPtr;

// ---------------------------------------------------------------- protocol
enum class Kind { Debug, Info, Warn, Error, Ask, Progress, Title, Choose, Launch, License, Background };

static std::wstring Trim(const std::wstring& s, const wchar_t* set = L" \t\r\n")
{
    size_t a = s.find_first_not_of(set), b = s.find_last_not_of(set);
    return a == std::wstring::npos ? L"" : s.substr(a, b - a + 1);
}

static std::wstring Utf8ToWide(const std::string& s)
{
    if (s.empty()) return L"";
    int n = MultiByteToWideChar(CP_UTF8, 0, s.data(), (int)s.size(), nullptr, 0);
    std::wstring w(n, L'\0');
    MultiByteToWideChar(CP_UTF8, 0, s.data(), (int)s.size(), w.data(), n);
    return w;
}

static std::string WideToUtf8(const std::wstring& w)
{
    if (w.empty()) return "";
    int n = WideCharToMultiByte(CP_UTF8, 0, w.data(), (int)w.size(), nullptr, 0, nullptr, nullptr);
    std::string s(n, '\0');
    WideCharToMultiByte(CP_UTF8, 0, w.data(), (int)w.size(), s.data(), n, nullptr, nullptr);
    return s;
}

// A line of install.bat output starting with ".<command>" controls the UI. Anything else (including unknown
// ".xxx" lines such as ".\tool.exe" output) is plain output, shown as debug.
static void ParseLine(const std::wstring& line, Kind& kind, std::wstring& text)
{
    kind = Kind::Debug;
    text = line;
    if (line.size() < 2 || line[0] != L'.') return;
    size_t i = 1;
    while (i < line.size() && iswalpha(line[i])) i++;
    if (i < line.size() && line[i] != L' ' && line[i] != L'\t') return;
    std::wstring word = line.substr(1, i - 1);
    std::transform(word.begin(), word.end(), word.begin(), ::towlower);
    static const struct { const wchar_t* name; Kind kind; } table[] = {
        { L"debug", Kind::Debug }, { L"info", Kind::Info }, { L"warn", Kind::Warn }, { L"error", Kind::Error },
        { L"ask", Kind::Ask }, { L"progress", Kind::Progress }, { L"title", Kind::Title }, { L"choose", Kind::Choose }, { L"launch", Kind::Launch }, { L"license", Kind::License }, { L"background", Kind::Background } };
    for (auto& entry : table)
        if (word == entry.name)
        {
            kind = entry.kind;
            text = Trim(Trim(line.substr(i)), L"'\"");
            return;
        }
}

// ".ask Question | Yes label | No label" (labels optional, '|' only) and ".choose Prompt | A | B | C" ('|' or ';', 2 to 5 options).
constexpr size_t MaxChoices = 5;
static void ParseChoices(const std::wstring& text, bool yesNo, std::wstring& prompt, std::vector<std::wstring>& options)
{
    wchar_t separator = (!yesNo && text.find(L'|') == std::wstring::npos) ? L';' : L'|';
    std::vector<std::wstring> parts;
    size_t start = 0;
    for (;;)
    {
        size_t at = text.find(separator, start);
        parts.push_back(Trim(text.substr(start, at == std::wstring::npos ? std::wstring::npos : at - start)));
        if (at == std::wstring::npos) break;
        start = at + 1;
    }
    prompt = parts[0];
    options.clear();
    for (size_t i = 1; i < parts.size() && options.size() < MaxChoices; i++)
        if (!parts[i].empty()) options.push_back(parts[i]);
    if (yesNo && options.size() != 2) options = { L"Yes", L"No" };
}

static int ParsePercent(const std::wstring& text)
{
    std::wstring t = Trim(text, L"% \t");
    if (t.empty() || t.size() > 4) return -1;
    for (wchar_t c : t) if (c < L'0' || c > L'9') return -1;
    return std::min(100, _wtoi(t.c_str()));
}

// ---------------------------------------------------------------- script runner
static HANDLE g_job = nullptr;
static std::atomic<bool> g_cancelled{ false };

typedef std::function<void(Kind, const std::wstring&, bool)> LineHandler;
// Returns the 1-based number of the chosen option, 0 when cancelled. Yes/no questions: 1 = yes, anything else = no.
typedef std::function<int(const std::wstring&, const std::vector<std::wstring>&, bool, const std::wstring&)> AskHandler;   // prompt, options, yes/no, body (license text)

// Reads a text file as UTF-8 (BOM optional) or UTF-16 LE (BOM); empty when it cannot be read.
static std::wstring ReadTextFile(const std::wstring& path)
{
    HANDLE f = CreateFileW(path.c_str(), GENERIC_READ, FILE_SHARE_READ, nullptr, OPEN_EXISTING, FILE_ATTRIBUTE_NORMAL, nullptr);
    if (f == INVALID_HANDLE_VALUE) return L"";
    LARGE_INTEGER size{};
    std::string bytes;
    if (GetFileSizeEx(f, &size) && size.QuadPart > 0 && size.QuadPart < (8 << 20))
    {
        bytes.resize((size_t)size.QuadPart);
        DWORD got = 0;
        if (!ReadFile(f, bytes.data(), (DWORD)bytes.size(), &got, nullptr)) got = 0;
        bytes.resize(got);
    }
    CloseHandle(f);
    std::wstring text;
    if (bytes.size() >= 2 && (unsigned char)bytes[0] == 0xFF && (unsigned char)bytes[1] == 0xFE)
        text.assign((const wchar_t*)(bytes.data() + 2), (bytes.size() - 2) / 2);
    else
        text = Utf8ToWide(bytes.size() >= 3 && (unsigned char)bytes[0] == 0xEF && (unsigned char)bytes[1] == 0xBB && (unsigned char)bytes[2] == 0xBF ? bytes.substr(3) : bytes);
    std::wstring out;
    for (wchar_t c : text) if (c != L'\r') out += c;
    return out;
}

// A path from the script may be relative to the script's folder.
static std::wstring ResolveNextToScript(const std::wstring& script, const std::wstring& path)
{
    if (path.size() > 1 && (path[1] == L':' || path[0] == L'\\' && path[1] == L'\\')) return path;
    size_t slash = script.find_last_of(L"\\/");
    return slash == std::wstring::npos ? path : script.substr(0, slash + 1) + path;
}

// Reads lines until the script's own process has exited and the pipe is drained. It must not wait for EOF: a program
// the script starts (for example "start app.exe") inherits the pipe and would keep the installer window open.
static void ReadLines(HANDLE pipe, HANDLE process, const std::function<void(const std::wstring&)>& onLine)
{
    std::string pending;
    char buffer[4096];
    for (;;)
    {
        DWORD available = 0;
        if (!PeekNamedPipe(pipe, nullptr, 0, nullptr, &available, nullptr)) break;
        if (available == 0)
        {
            if (WaitForSingleObject(process, 15) != WAIT_OBJECT_0) continue;
            if (!PeekNamedPipe(pipe, nullptr, 0, nullptr, &available, nullptr) || available == 0) break;
        }
        DWORD got = 0;
        if (!ReadFile(pipe, buffer, std::min<DWORD>(available, sizeof buffer), &got, nullptr) || got == 0) break;
        pending.append(buffer, got);
        size_t start = 0, nl;
        while ((nl = pending.find('\n', start)) != std::string::npos)
        {
            std::string line = pending.substr(start, nl - start);
            if (!line.empty() && line.back() == '\r') line.pop_back();
            onLine(Utf8ToWide(line));
            start = nl + 1;
        }
        pending.erase(0, start);
    }
    if (!pending.empty()) onLine(Utf8ToWide(pending));
}

static int RunScript(const std::wstring& script, const std::wstring& extra, const LineHandler& onLine, const AskHandler& onAsk)
{
    SECURITY_ATTRIBUTES sa{ sizeof sa, nullptr, TRUE };
    HANDLE outR, outW, errR, errW, inR, inW;
    CreatePipe(&outR, &outW, &sa, 0); SetHandleInformation(outR, HANDLE_FLAG_INHERIT, 0);
    CreatePipe(&errR, &errW, &sa, 0); SetHandleInformation(errR, HANDLE_FLAG_INHERIT, 0);
    CreatePipe(&inR, &inW, &sa, 0); SetHandleInformation(inW, HANDLE_FLAG_INHERIT, 0);

    STARTUPINFOW si{};
    si.cb = sizeof si;
    si.dwFlags = STARTF_USESTDHANDLES | STARTF_USESHOWWINDOW;
    si.wShowWindow = SW_HIDE;
    si.hStdInput = inR; si.hStdOutput = outW; si.hStdError = errW;
    std::wstring command = L"cmd.exe /d /s /c \"chcp 65001>nul & call \"" + script + L"\" " + extra + L"\"";
    std::wstring dir = script.substr(0, script.find_last_of(L"\\/"));
    PROCESS_INFORMATION pi{};
    if (!CreateProcessW(nullptr, command.data(), nullptr, nullptr, TRUE, CREATE_NO_WINDOW | CREATE_SUSPENDED, nullptr, dir.c_str(), &si, &pi))
    {
        onLine(Kind::Error, L"Could not start the install script.", false);
        return 1;
    }
    CloseHandle(outW); CloseHandle(errW); CloseHandle(inR);

    g_job = CreateJobObjectW(nullptr, nullptr);
    // No kill-on-close: programs the script launches (e.g. the installed app) must outlive the installer.
    // The job only exists so Cancel can stop the whole script tree.
    AssignProcessToJobObject(g_job, pi.hProcess);
    ResumeThread(pi.hThread);

    std::thread errors([&] { ReadLines(errR, pi.hProcess, [&](const std::wstring& line) { onLine(Kind::Debug, line, true); }); });
    ReadLines(outR, pi.hProcess, [&](const std::wstring& line)
    {
        Kind kind; std::wstring text;
        ParseLine(line, kind, text);
        if (kind == Kind::Background)
        {
            onLine(kind, ResolveNextToScript(script, Trim(text, L" \t")), false);
            return;
        }
        if (kind == Kind::Ask || kind == Kind::Choose || kind == Kind::License)
        {
            const bool yesNo = kind != Kind::Choose;
            std::wstring prompt, body;
            std::vector<std::wstring> options;
            int picked = 0;
            if (kind == Kind::License)
            {
                // ".license file.txt | I agree | Decline": the user must accept before the script goes on; answer yes/no
                ParseChoices(text, true, prompt, options);
                std::wstring file = ResolveNextToScript(script, Trim(prompt, L" \t")), label = L"License agreement";
                body = ReadTextFile(file);
                if (text.find(L'|') == std::wstring::npos) options = { L"I agree", L"Decline" };
                if (body.empty()) { onLine(Kind::Error, L"license file not found or empty: " + file, false); picked = 2; }
                else picked = onAsk(label, options, true, body);
            }
            else
            {
                ParseChoices(text, yesNo, prompt, options);
                if (!yesNo && options.size() < 2) onLine(Kind::Error, L"choose needs a prompt and 2 to 5 options separated by | or ;", false);
                else picked = onAsk(prompt, options, yesNo, L"");
            }
            std::string answer = yesNo ? (picked == 1 ? "yes" : "no") : std::to_string(picked);
            answer += (char)13;
            answer += (char)10;
            DWORD written;
            WriteFile(inW, answer.data(), (DWORD)answer.size(), &written, nullptr);
        }
        else onLine(kind, text, false);
    });
    WaitForSingleObject(pi.hProcess, INFINITE);
    DWORD code = 0;
    GetExitCodeProcess(pi.hProcess, &code);
    errors.join();
    CloseHandle(outR); CloseHandle(errR); CloseHandle(inW);
    CloseHandle(pi.hProcess); CloseHandle(pi.hThread);
    CloseHandle(g_job);
    g_job = nullptr;
    return g_cancelled ? 2 : (int)code;
}

// ---------------------------------------------------------------- UI model
enum class StepState { Active, Done, Warn, Error };
struct Step { StepState state; std::wstring text; };

struct Region { D2D1_RECT_F rect; int id; };
enum { BtnAction = 1, BtnDetails, BtnYes, BtnNo, BtnLaunch, BtnChoice = 100 };   // BtnChoice + i: option i of a .choose sheet

static struct Model
{
    std::wstring title, scriptName;
    std::wstring launchPath, launchLabel;   // ".launch path | label": offered as a button once the install succeeded
    std::vector<Step> steps;
    int percent = -1;
    std::deque<std::wstring> log;
    int logScroll = 0;
    bool details = false, finished = false, ask = false, cancelConfirm = false;
    std::wstring askText, askBody, bgPath;   // askBody: license text (non-empty = license sheet); bgPath: ".background" image
    float licScroll = 0.f, licMax = 0.f;
    std::vector<std::wstring> choices;   // option labels of the open sheet; yes/no sheets use [yes label, no label]
    bool yesNo = true;
    int exitCode = 0;
    int hover = 0;
    ULONGLONG askTick = 0, finishTick = 0, tickTick = 0;
    std::wstring tickCur, tickPrev;
} g;

struct Event { Kind kind; std::wstring text; bool fromStderr; };
struct AskRequest { std::wstring text; std::vector<std::wstring> options; bool yesNo; std::wstring body; HANDLE done; int answer; };
constexpr UINT WM_EVENT = WM_APP + 1, WM_ASK = WM_APP + 2, WM_DONE = WM_APP + 3;
constexpr UINT_PTR TimerAnimate = 1, TimerClose = 2;

static HWND g_hwnd;
static bool g_glass = false, g_autoClose = false;
static UINT g_dpi = 96;
static AskRequest* g_pendingAsk = nullptr;
static std::vector<Region> g_regions;

// ---------------------------------------------------------------- graphics
static ComPtr<ID3D11Device> d3d;
static ComPtr<IDXGISwapChain1> swap;
static ComPtr<ID2D1Factory1> d2dFactory;
static ComPtr<ID2D1DeviceContext> dc;
static ComPtr<ID2D1Bitmap1> targetBitmap;
static ComPtr<IDWriteFactory> dwrite;
static ComPtr<IDCompositionDevice> dcompDevice;
static ComPtr<IDCompositionTarget> dcompTarget;
static ComPtr<IDCompositionVisual> dcompVisual;
static ComPtr<ID2D1SolidColorBrush> brush;
static ComPtr<ID2D1StrokeStyle> roundStroke;
static ComPtr<IDWriteTextFormat> fTitle, fSub, fBody, fSmall, fMono, fButton, fButtonLarge, fButtonBold, fCenter, fPercent, fAsk, fAskIcon, fLicense;

constexpr float WidthDip = 520.f, HeightDip = 424.f, Pad = 32.f;
static float Scale() { return g_dpi / 96.f; }

static ComPtr<IDWriteTextFormat> MakeFormat(const wchar_t* family, float size, DWRITE_FONT_WEIGHT weight, bool ellipsis, bool center = false)
{
    ComPtr<IDWriteTextFormat> f;
    dwrite->CreateTextFormat(family, nullptr, weight, DWRITE_FONT_STYLE_NORMAL, DWRITE_FONT_STRETCH_NORMAL, size, L"en-us", &f);
    if (ellipsis)
    {
        f->SetWordWrapping(DWRITE_WORD_WRAPPING_NO_WRAP);
        ComPtr<IDWriteInlineObject> sign;
        dwrite->CreateEllipsisTrimmingSign(f.Get(), &sign);
        DWRITE_TRIMMING trimming{ DWRITE_TRIMMING_GRANULARITY_CHARACTER, 0, 0 };
        f->SetTrimming(&trimming, sign.Get());
    }
    if (center)
    {
        f->SetTextAlignment(DWRITE_TEXT_ALIGNMENT_CENTER);
        f->SetParagraphAlignment(DWRITE_PARAGRAPH_ALIGNMENT_CENTER);
    }
    return f;
}

static void CreateTargetBitmap()
{
    ComPtr<IDXGISurface> surface;
    swap->GetBuffer(0, IID_PPV_ARGS(&surface));
    D2D1_BITMAP_PROPERTIES1 props = D2D1::BitmapProperties1(D2D1_BITMAP_OPTIONS_TARGET | D2D1_BITMAP_OPTIONS_CANNOT_DRAW,
        D2D1::PixelFormat(DXGI_FORMAT_B8G8R8A8_UNORM, D2D1_ALPHA_MODE_PREMULTIPLIED), (float)g_dpi, (float)g_dpi);
    dc->CreateBitmapFromDxgiSurface(surface.Get(), &props, &targetBitmap);
    dc->SetTarget(targetBitmap.Get());
    dc->SetDpi((float)g_dpi, (float)g_dpi);
}

static bool InitGraphics(int w, int h)
{
    UINT flags = D3D11_CREATE_DEVICE_BGRA_SUPPORT;
    if (FAILED(D3D11CreateDevice(nullptr, D3D_DRIVER_TYPE_HARDWARE, nullptr, flags, nullptr, 0, D3D11_SDK_VERSION, &d3d, nullptr, nullptr)) &&
        FAILED(D3D11CreateDevice(nullptr, D3D_DRIVER_TYPE_WARP, nullptr, flags, nullptr, 0, D3D11_SDK_VERSION, &d3d, nullptr, nullptr)))
        return false;
    ComPtr<IDXGIDevice> dxgiDevice;
    d3d.As(&dxgiDevice);
    ComPtr<IDXGIAdapter> adapter;
    dxgiDevice->GetAdapter(&adapter);
    ComPtr<IDXGIFactory2> factory;
    adapter->GetParent(IID_PPV_ARGS(&factory));

    DXGI_SWAP_CHAIN_DESC1 desc{};
    desc.Width = w; desc.Height = h;
    desc.Format = DXGI_FORMAT_B8G8R8A8_UNORM;
    desc.SampleDesc.Count = 1;
    desc.BufferUsage = DXGI_USAGE_RENDER_TARGET_OUTPUT;
    desc.BufferCount = 2;
    desc.SwapEffect = DXGI_SWAP_EFFECT_FLIP_SEQUENTIAL;
    desc.AlphaMode = DXGI_ALPHA_MODE_PREMULTIPLIED;
    if (FAILED(factory->CreateSwapChainForComposition(d3d.Get(), &desc, nullptr, &swap))) return false;

    D2D1_FACTORY_OPTIONS options{};
    if (FAILED(D2D1CreateFactory(D2D1_FACTORY_TYPE_SINGLE_THREADED, __uuidof(ID2D1Factory1), &options, (void**)d2dFactory.GetAddressOf()))) return false;
    ComPtr<ID2D1Device> d2dDevice;
    d2dFactory->CreateDevice(dxgiDevice.Get(), &d2dDevice);
    d2dDevice->CreateDeviceContext(D2D1_DEVICE_CONTEXT_OPTIONS_NONE, &dc);
    dc->SetTextAntialiasMode(D2D1_TEXT_ANTIALIAS_MODE_GRAYSCALE);
    dc->CreateSolidColorBrush(D2D1::ColorF(0, 0), &brush);
    D2D1_STROKE_STYLE_PROPERTIES stroke = D2D1::StrokeStyleProperties(D2D1_CAP_STYLE_ROUND, D2D1_CAP_STYLE_ROUND, D2D1_CAP_STYLE_ROUND);
    d2dFactory->CreateStrokeStyle(stroke, nullptr, 0, &roundStroke);

    DWriteCreateFactory(DWRITE_FACTORY_TYPE_SHARED, __uuidof(IDWriteFactory), (IUnknown**)dwrite.GetAddressOf());
    BOOL hasVariable = FALSE;
    {
        ComPtr<IDWriteFontCollection> fonts;
        UINT32 index;
        dwrite->GetSystemFontCollection(&fonts);
        fonts->FindFamilyName(L"Segoe UI Variable Text", &index, &hasVariable);
    }
    const wchar_t* family = hasVariable ? L"Segoe UI Variable Text" : L"Segoe UI";
    fTitle = MakeFormat(family, 22, DWRITE_FONT_WEIGHT_SEMI_BOLD, true);
    fSub = MakeFormat(family, 13, DWRITE_FONT_WEIGHT_NORMAL, true);
    fBody = MakeFormat(family, 14, DWRITE_FONT_WEIGHT_NORMAL, true);
    fSmall = MakeFormat(family, 13, DWRITE_FONT_WEIGHT_NORMAL, true);
    fMono = MakeFormat(L"Consolas", 12, DWRITE_FONT_WEIGHT_NORMAL, true);
    fButton = MakeFormat(family, 13, DWRITE_FONT_WEIGHT_SEMI_BOLD, false, true);
    fButtonLarge = MakeFormat(family, 15, DWRITE_FONT_WEIGHT_NORMAL, false, true);
    fButtonBold = MakeFormat(family, 15, DWRITE_FONT_WEIGHT_SEMI_BOLD, false, true);
    fCenter = MakeFormat(family, 15, DWRITE_FONT_WEIGHT_NORMAL, false, true);
    fPercent = MakeFormat(family, 28, DWRITE_FONT_WEIGHT_LIGHT, true);
    fPercent->SetTextAlignment(DWRITE_TEXT_ALIGNMENT_TRAILING);
    fAsk = MakeFormat(family, 15, DWRITE_FONT_WEIGHT_NORMAL, false);
    fLicense = MakeFormat(family, 12.5f, DWRITE_FONT_WEIGHT_NORMAL, false);
    fAskIcon = MakeFormat(family, 22, DWRITE_FONT_WEIGHT_SEMI_BOLD, false, true);

    if (FAILED(DCompositionCreateDevice(dxgiDevice.Get(), IID_PPV_ARGS(&dcompDevice)))) return false;
    dcompDevice->CreateTargetForHwnd(g_hwnd, TRUE, &dcompTarget);
    dcompDevice->CreateVisual(&dcompVisual);
    dcompVisual->SetContent(swap.Get());
    dcompTarget->SetRoot(dcompVisual.Get());
    dcompDevice->Commit();
    CreateTargetBitmap();
    return true;
}

static void ResizeGraphics(int w, int h)
{
    if (!swap) return;
    dc->SetTarget(nullptr);
    targetBitmap.Reset();
    swap->ResizeBuffers(0, w, h, DXGI_FORMAT_UNKNOWN, 0);
    CreateTargetBitmap();
}

// ---------------------------------------------------------------- layout and drawing
// Fixed-size window: the card shows either the step list or the details log, so nothing ever resizes.
// How much white is laid over the Acrylic: lower = more see-through, higher = more readable.
constexpr float GlassTint = 0.05f, CardGlass = 0.30f;
constexpr float CardTop = 100.f, CardH = 196.f, RowH = 36.f, BarY = 314.f, LogY = 332.f, FootY = 360.f;
constexpr int MaxVisibleSteps = 5;
static float g_shown = 0.f;   // eased progress actually drawn

static float Px(float v) { float s = Scale(); return roundf(v * s) / s; }
static float Hair() { return 1.f / Scale(); }
static float Clamp01(float v) { return v < 0.f ? 0.f : v > 1.f ? 1.f : v; }
static float EaseOut(float t) { t = 1.f - Clamp01(t); return 1.f - t * t * t; }
static float Since(ULONGLONG start, float ms) { return start ? Clamp01((float)(GetTickCount64() - start) / ms) : 1.f; }
static float Lerp(float a, float b, float t) { return a + (b - a) * t; }

static bool Animating()
{
    ULONGLONG now = GetTickCount64();
    return !g.finished || g.ask || now - g.finishTick < 2000 || now - g.tickTick < 300 || now - g.askTick < 400;
}

static void Wake() { SetTimer(g_hwnd, TimerAnimate, 16, nullptr); }

static void Relayout()
{
    int w = (int)lroundf(WidthDip * Scale()), h = (int)lroundf(HeightDip * Scale());
    RECT r;
    GetWindowRect(g_hwnd, &r);
    if (r.right - r.left == w && r.bottom - r.top == h) return;
    SetWindowPos(g_hwnd, nullptr, 0, 0, w, h, SWP_NOMOVE | SWP_NOZORDER | SWP_NOACTIVATE);
    ResizeGraphics(w, h);
}

static void SetColor(float r, float g_, float b, float a = 1.f) { brush->SetColor(D2D1::ColorF(r / 255.f, g_ / 255.f, b / 255.f, a)); }
static void Ink() { SetColor(29, 29, 31); }
static void Muted() { SetColor(95, 99, 104); }
static void Blue() { SetColor(10, 132, 255); }

struct Stop { float pos, r, g, b, a; };
static ComPtr<ID2D1LinearGradientBrush> Linear(float x0, float y0, float x1, float y1, std::initializer_list<Stop> stops, float opacity = 1.f)
{
    std::vector<D2D1_GRADIENT_STOP> v;
    for (auto& s : stops) v.push_back({ s.pos, D2D1::ColorF(s.r / 255.f, s.g / 255.f, s.b / 255.f, s.a) });
    ComPtr<ID2D1GradientStopCollection> collection;
    dc->CreateGradientStopCollection(v.data(), (UINT32)v.size(), &collection);
    ComPtr<ID2D1LinearGradientBrush> b;
    dc->CreateLinearGradientBrush(D2D1::LinearGradientBrushProperties(D2D1::Point2F(x0, y0), D2D1::Point2F(x1, y1)), collection.Get(), &b);
    b->SetOpacity(opacity);
    return b;
}

static void Text(const std::wstring& s, IDWriteTextFormat* f, float x, float y, float w, float h)
{
    dc->DrawText(s.c_str(), (UINT32)s.size(), f, D2D1::RectF(x, y, x + w, y + h), brush.Get(), D2D1_DRAW_TEXT_OPTIONS_CLIP);
}

static D2D1_ROUNDED_RECT RR(float l, float t, float r, float b, float radius) { return D2D1::RoundedRect(D2D1::RectF(l, t, r, b), radius, radius); }

static void SoftShadow(float l, float t, float r, float b, float radius, float dy, float spread)
{
    for (int i = 14; i >= 1; i--)
    {
        float grow = i * spread / 14.f;
        SetColor(20, 30, 60, 0.012f);
        dc->FillRoundedRectangle(RR(l - grow, t - grow + dy, r + grow, b + grow + dy, radius + grow), brush.Get());
    }
}

// Milky glass over Acrylic, plain white otherwise; used by the grouped card and the secondary button.
static void Surface(const D2D1_ROUNDED_RECT& rr, float glassAlpha)
{
    SetColor(255, 255, 255, g_glass ? glassAlpha : 1.f);
    dc->FillRoundedRectangle(rr, brush.Get());
    D2D1_ROUNDED_RECT edge = rr;
    float h = Hair() / 2;
    edge.rect = D2D1::RectF(rr.rect.left + h, rr.rect.top + h, rr.rect.right - h, rr.rect.bottom - h);
    if (g_glass) SetColor(255, 255, 255, 0.75f); else SetColor(0, 0, 0, 0.09f);
    dc->DrawRoundedRectangle(edge, brush.Get(), Hair());
}

static void Pill(float x, float y, float w, float h, const std::wstring& label, int id, bool primary)
{
    bool hot = g.hover == id;
    D2D1_ROUNDED_RECT rr = RR(x, y, x + w, y + h, h / 2);
    if (primary)
    {
        auto grad = Linear(x, y, x + w, y + h, { { 0.f, 10, 132, 255, 1.f }, { 1.f, 94, 92, 230, 1.f } }, hot ? 0.88f : 1.f);
        dc->FillRoundedRectangle(rr, grad.Get());
        SetColor(255, 255, 255);
    }
    else
    {
        Surface(rr, hot ? 0.85f : 0.6f);
        Ink();
    }
    Text(label, fButton.Get(), x, y, w, h);
    g_regions.push_back({ D2D1::RectF(x, y, x + w, y + h), id });
}

// ".background image.png": decoded once (WIC: png, jpg, bmp, gif...) and drawn cover-fit under a white veil so text stays readable.
static ComPtr<ID2D1Bitmap1> g_bgBitmap;
static std::wstring g_bgLoaded;
static void DrawBackgroundImage()
{
    if (g.bgPath != g_bgLoaded)
    {
        g_bgLoaded = g.bgPath;
        g_bgBitmap.Reset();
        ComPtr<IWICImagingFactory> wic;
        ComPtr<IWICBitmapDecoder> decoder;
        ComPtr<IWICBitmapFrameDecode> frame;
        ComPtr<IWICFormatConverter> converter;
        CoInitializeEx(nullptr, COINIT_APARTMENTTHREADED);
        if (!g.bgPath.empty() && SUCCEEDED(CoCreateInstance(CLSID_WICImagingFactory, nullptr, CLSCTX_INPROC_SERVER, IID_PPV_ARGS(&wic))) &&
            SUCCEEDED(wic->CreateDecoderFromFilename(g.bgPath.c_str(), nullptr, GENERIC_READ, WICDecodeMetadataCacheOnDemand, &decoder)) &&
            SUCCEEDED(decoder->GetFrame(0, &frame)) && SUCCEEDED(wic->CreateFormatConverter(&converter)) &&
            SUCCEEDED(converter->Initialize(frame.Get(), GUID_WICPixelFormat32bppPBGRA, WICBitmapDitherTypeNone, nullptr, 0.f, WICBitmapPaletteTypeMedianCut)))
        {
            ComPtr<ID2D1Bitmap> plain;
            if (SUCCEEDED(dc->CreateBitmapFromWicBitmap(converter.Get(), nullptr, &plain))) plain.As(&g_bgBitmap);
        }
    }
    if (!g_bgBitmap) return;
    D2D1_SIZE_F size = g_bgBitmap->GetSize();
    float k = std::max(WidthDip / size.width, HeightDip / size.height), w = size.width * k, h = size.height * k;
    dc->DrawBitmap(g_bgBitmap.Get(), D2D1::RectF((WidthDip - w) / 2, (HeightDip - h) / 2, (WidthDip + w) / 2, (HeightDip + h) / 2));
    SetColor(255, 255, 255, 0.62f);
    dc->FillRectangle(D2D1::RectF(0, 0, WidthDip, HeightDip), brush.Get());
}

static void DrawStepIcon(const Step& s, float cx, float cy, float angle)
{
    const float k = 1.25f;
    if (s.state == StepState::Active)
    {
        const float r = 7.5f;
        ComPtr<ID2D1PathGeometry> path;
        d2dFactory->CreatePathGeometry(&path);
        ComPtr<ID2D1GeometrySink> sink;
        path->Open(&sink);
        float a0 = angle, a1 = angle + 4.4f;
        sink->BeginFigure(D2D1::Point2F(cx + r * cosf(a0), cy + r * sinf(a0)), D2D1_FIGURE_BEGIN_HOLLOW);
        sink->AddArc(D2D1::ArcSegment(D2D1::Point2F(cx + r * cosf(a1), cy + r * sinf(a1)), D2D1::SizeF(r, r), 0.f,
            D2D1_SWEEP_DIRECTION_CLOCKWISE, D2D1_ARC_SIZE_LARGE));
        sink->EndFigure(D2D1_FIGURE_END_OPEN);
        sink->Close();
        SetColor(10, 132, 255, 0.16f);
        dc->DrawEllipse(D2D1::Ellipse(D2D1::Point2F(cx, cy), r, r), brush.Get(), 2.4f);
        auto grad = Linear(cx - r, cy - r, cx + r, cy + r, { { 0.f, 10, 132, 255, 1.f }, { 1.f, 191, 90, 242, 1.f } });
        dc->DrawGeometry(path.Get(), grad.Get(), 2.4f, roundStroke.Get());
        return;
    }
    if (s.state == StepState::Done) SetColor(48, 209, 88);
    else if (s.state == StepState::Warn) SetColor(255, 159, 10);
    else SetColor(255, 69, 58);
    dc->FillEllipse(D2D1::Ellipse(D2D1::Point2F(cx, cy), 10.f, 10.f), brush.Get());
    SetColor(255, 255, 255);
    const float sw = 2.2f;
    if (s.state == StepState::Done)
    {
        dc->DrawLine(D2D1::Point2F(cx - 3.6f * k, cy + 0.2f * k), D2D1::Point2F(cx - 1.0f * k, cy + 3.0f * k), brush.Get(), sw, roundStroke.Get());
        dc->DrawLine(D2D1::Point2F(cx - 1.0f * k, cy + 3.0f * k), D2D1::Point2F(cx + 3.8f * k, cy - 2.8f * k), brush.Get(), sw, roundStroke.Get());
    }
    else if (s.state == StepState::Warn)
    {
        dc->DrawLine(D2D1::Point2F(cx, cy - 4.6f), D2D1::Point2F(cx, cy + 1.0f), brush.Get(), sw, roundStroke.Get());
        dc->FillEllipse(D2D1::Ellipse(D2D1::Point2F(cx, cy + 4.4f), 1.3f, 1.3f), brush.Get());
    }
    else
    {
        dc->DrawLine(D2D1::Point2F(cx - 3.8f, cy - 3.8f), D2D1::Point2F(cx + 3.8f, cy + 3.8f), brush.Get(), sw, roundStroke.Get());
        dc->DrawLine(D2D1::Point2F(cx + 3.8f, cy - 3.8f), D2D1::Point2F(cx - 3.8f, cy + 3.8f), brush.Get(), sw, roundStroke.Get());
    }
}

// The app badge: a blue-violet tile with a download glyph while running; on success it turns green, the glyph
// becomes a drawn check, a ripple expands and confetti dots burst out. Failure turns it red with a cross.
static void DrawBadge(float bx, float by, bool failed)
{
    const float size = 48.f, cx = bx + size / 2, cy = by + size / 2;
    float t = g.finished ? Since(g.finishTick, 1100.f) : 0.f;
    bool ok = g.finished && !failed && !g_cancelled;

    if (ok)
    {
        static const float palette[8][3] = { { 10, 132, 255 }, { 94, 92, 230 }, { 191, 90, 242 }, { 255, 55, 95 }, { 255, 159, 10 }, { 48, 209, 88 }, { 100, 210, 255 }, { 255, 214, 10 } };
        float burst = EaseOut(Clamp01((t - 0.12f) / 0.88f));
        for (int i = 0; i < 8; i++)
        {
            float a = i * 0.7853982f + 0.3f, d = 30.f + 26.f * burst, r = 3.6f * (1.f - burst) + 0.4f;
            SetColor(palette[i][0], palette[i][1], palette[i][2], 1.f - burst * 0.7f);
            if (burst < 1.f) dc->FillEllipse(D2D1::Ellipse(D2D1::Point2F(cx + d * cosf(a), cy + d * sinf(a)), r, r), brush.Get());
        }
        float ripple = EaseOut(Clamp01((t - 0.05f) / 0.7f));
        if (ripple < 1.f)
        {
            SetColor(48, 209, 88, 0.45f * (1.f - ripple));
            dc->DrawRoundedRectangle(RR(bx - 22 * ripple, by - 22 * ripple, bx + size + 22 * ripple, by + size + 22 * ripple, 14.f + 22 * ripple), brush.Get(), 2.f);
        }
    }

    float pop = ok ? 1.f + 0.12f * sinf(Clamp01(t / 0.45f) * 3.14159f) : 1.f;
    float half = size / 2 * pop;
    D2D1_ROUNDED_RECT tile = RR(cx - half, cy - half, cx + half, cy + half, 14.f * pop);
    SoftShadow(tile.rect.left, tile.rect.top, tile.rect.right, tile.rect.bottom, 14.f, 5.f, 9.f);
    auto blue = Linear(bx, by, bx + size, by + size, { { 0.f, 10, 132, 255, 1.f }, { 1.f, 94, 92, 230, 1.f } });
    dc->FillRoundedRectangle(tile, blue.Get());
    float over = g.finished ? EaseOut(Clamp01(t * 2.2f)) : 0.f;
    if (over > 0.f)
    {
        if (ok)
        {
            auto green = Linear(bx, by, bx + size, by + size, { { 0.f, 52, 199, 89, 1.f }, { 1.f, 48, 209, 180, 1.f } }, over);
            dc->FillRoundedRectangle(tile, green.Get());
        }
        else { SetColor(255, 69, 58, over); dc->FillRoundedRectangle(tile, brush.Get()); }
    }

    SetColor(255, 255, 255);
    float glyph = 1.f - Clamp01(t * 4.f);
    if (glyph > 0.f)
    {
        SetColor(255, 255, 255, glyph);
        dc->DrawLine(D2D1::Point2F(cx, cy - 10), D2D1::Point2F(cx, cy + 5), brush.Get(), 2.8f, roundStroke.Get());
        dc->DrawLine(D2D1::Point2F(cx - 7, cy - 1.5f), D2D1::Point2F(cx, cy + 6), brush.Get(), 2.8f, roundStroke.Get());
        dc->DrawLine(D2D1::Point2F(cx + 7, cy - 1.5f), D2D1::Point2F(cx, cy + 6), brush.Get(), 2.8f, roundStroke.Get());
        dc->DrawLine(D2D1::Point2F(cx - 10, cy + 12), D2D1::Point2F(cx + 10, cy + 12), brush.Get(), 2.8f, roundStroke.Get());
    }
    if (g.finished && t > 0.2f)
    {
        float p = EaseOut(Clamp01((t - 0.2f) / 0.5f));
        SetColor(255, 255, 255);
        if (ok)
        {
            D2D1_POINT_2F a = D2D1::Point2F(cx - 10, cy + 1), b = D2D1::Point2F(cx - 3.5f, cy + 8), c = D2D1::Point2F(cx + 10, cy - 8);
            const float l1 = 9.5f, l2 = 19.f, drawn = p * (l1 + l2);
            if (drawn > 0) dc->DrawLine(a, drawn >= l1 ? b : D2D1::Point2F(Lerp(a.x, b.x, drawn / l1), Lerp(a.y, b.y, drawn / l1)), brush.Get(), 3.4f, roundStroke.Get());
            if (drawn > l1) { float u = (drawn - l1) / l2; dc->DrawLine(b, D2D1::Point2F(Lerp(b.x, c.x, u), Lerp(b.y, c.y, u)), brush.Get(), 3.4f, roundStroke.Get()); }
        }
        else
        {
            float e = 9.f * p;
            dc->DrawLine(D2D1::Point2F(cx - e, cy - e), D2D1::Point2F(cx + e, cy + e), brush.Get(), 3.2f, roundStroke.Get());
            dc->DrawLine(D2D1::Point2F(cx + e, cy - e), D2D1::Point2F(cx - e, cy + e), brush.Get(), 3.2f, roundStroke.Get());
        }
    }
}

// One-line rolling log: the newest output slides up from below while the previous line fades out above.
static void DrawTicker()
{
    const float h = 18.f, w = WidthDip - 2 * Pad;
    float t = EaseOut(Since(g.tickTick, 240.f));
    dc->PushAxisAlignedClip(D2D1::RectF(Pad, LogY, Pad + w, LogY + h), D2D1_ANTIALIAS_MODE_ALIASED);
    if (t < 1.f && !g.tickPrev.empty())
    {
        Muted();
        brush->SetOpacity(1.f - t);
        Text(g.tickPrev, fMono.Get(), Pad, LogY - h * t, w, h);
        brush->SetOpacity(1.f);
    }
    if (!g.tickCur.empty())
    {
        SetColor(95, 99, 104, 1.f);
        brush->SetOpacity(t);
        Text(g.tickCur, fMono.Get(), Pad, LogY + h * (1.f - t), w, h);
        brush->SetOpacity(1.f);
    }
    dc->PopAxisAlignedClip();
}

static float LabelWidth(const std::wstring& label)
{
    ComPtr<IDWriteTextLayout> layout;
    dwrite->CreateTextLayout(label.c_str(), (UINT32)label.size(), fButton.Get(), 400.f, 40.f, &layout);
    DWRITE_TEXT_METRICS metrics{};
    layout->GetMetrics(&metrics);
    return metrics.widthIncludingTrailingWhitespace;
}

// Question sheet: a glass panel that rises from the bottom edge, in the same language as the card and pills.
// ".license": a large sheet with the scrollable agreement text and two buttons (answer yes/no).
static void DrawLicenseSheet(float e)
{
    const float margin = 14.f, pad = 22.f, btnH = 40.f, x0 = margin, sw = WidthDip - 2 * margin;
    const float sh = HeightDip - 2 * margin, y0 = margin + (1.f - e) * 30.f;
    SoftShadow(x0, y0, x0 + sw, y0 + sh, 20.f, -4.f, 22.f);
    SetColor(255, 255, 255, 0.95f);
    dc->FillRoundedRectangle(RR(x0, y0, x0 + sw, y0 + sh, 20.f), brush.Get());
    Ink();
    Text(g.askText, fButtonBold.Get(), x0 + pad, y0 + 14, sw - 2 * pad, 28);

    const float ty = y0 + 50.f, th = sh - 50.f - btnH - 2 * pad + 6.f, tw = sw - 2 * pad - 10.f;
    SetColor(244, 245, 247, 0.9f);
    dc->FillRoundedRectangle(RR(x0 + pad - 8, ty - 4, x0 + sw - pad + 8, ty + th + 4, 10.f), brush.Get());
    ComPtr<IDWriteTextLayout> layout;
    dwrite->CreateTextLayout(g.askBody.c_str(), (UINT32)g.askBody.size(), fLicense.Get(), tw, 100000.f, &layout);
    DWRITE_TEXT_METRICS m{};
    layout->GetMetrics(&m);
    g.licMax = std::max(0.f, m.height - th);
    g.licScroll = std::max(0.f, std::min(g.licScroll, g.licMax));
    dc->PushAxisAlignedClip(D2D1::RectF(x0 + pad, ty, x0 + sw - pad - 10, ty + th), D2D1_ANTIALIAS_MODE_ALIASED);
    Ink();
    dc->DrawTextLayout(D2D1::Point2F(x0 + pad, ty - g.licScroll), layout.Get(), brush.Get());
    dc->PopAxisAlignedClip();
    if (g.licMax > 0.f)
    {
        float barH = std::max(24.f, th * th / m.height), barY = ty + (th - barH) * (g.licScroll / g.licMax);
        SetColor(60, 60, 67, 0.35f);
        dc->FillRoundedRectangle(RR(x0 + sw - pad - 3, barY, x0 + sw - pad + 1, barY + barH, 2.f), brush.Get());
    }
    float by = y0 + sh - pad - btnH + 6.f;
    float yesW = std::max(110.f, LabelWidth(g.choices[0]) + 40.f), noW = std::max(96.f, LabelWidth(g.choices[1]) + 40.f);
    Pill(x0 + sw - pad - yesW, by, yesW, btnH, g.choices[0], BtnYes, true);
    Pill(x0 + sw - pad - yesW - 12.f - noW, by, noW, btnH, g.choices[1], BtnNo, false);
}

static void DrawAskSheet()
{
    float e = EaseOut(Since(g.askTick, 260.f));
    SetColor(255, 255, 255, (g_glass ? 0.50f : 0.70f) * e);
    dc->FillRectangle(D2D1::RectF(0, 0, WidthDip, HeightDip), brush.Get());

    const float margin = 14.f, sw = WidthDip - 2 * margin, pad = 22.f, iconD = 40.f, btnH = 40.f;
    if (!g.askBody.empty()) { DrawLicenseSheet(e); return; }
    ComPtr<IDWriteTextLayout> layout;
    float textW = sw - 2 * pad - iconD - 16.f;
    dwrite->CreateTextLayout(g.askText.c_str(), (UINT32)g.askText.size(), fAsk.Get(), textW, 1000.f, &layout);
    DWRITE_TEXT_METRICS m{};
    layout->GetMetrics(&m);
    float textH = std::max(m.height, iconD);
    float sh = g.yesNo ? pad + textH + 18.f + btnH + pad - 4.f
                       : pad + textH + 16.f + g.choices.size() * (btnH + 8.f) - 8.f + pad - 4.f;
    float x0 = margin, y0 = HeightDip - margin - sh + (1.f - e) * (sh + 30.f);

    SoftShadow(x0, y0, x0 + sw, y0 + sh, 20.f, -4.f, 22.f);
    D2D1_ROUNDED_RECT sheet = RR(x0, y0, x0 + sw, y0 + sh, 20.f);
    SetColor(255, 255, 255, 0.92f);
    dc->FillRoundedRectangle(sheet, brush.Get());
    D2D1_ROUNDED_RECT edge = RR(x0 + Hair() / 2, y0 + Hair() / 2, x0 + sw - Hair() / 2, y0 + sh - Hair() / 2, 20.f);
    SetColor(60, 60, 67, 0.12f);
    dc->DrawRoundedRectangle(edge, brush.Get(), Hair());

    float ix = x0 + pad, iy = y0 + pad;
    auto grad = Linear(ix, iy, ix + iconD, iy + iconD, { { 0.f, 10, 132, 255, 1.f }, { 1.f, 94, 92, 230, 1.f } });
    dc->FillRoundedRectangle(RR(ix, iy, ix + iconD, iy + iconD, 12.f), grad.Get());
    SetColor(255, 255, 255);
    Text(L"?", fAskIcon.Get(), ix, iy, iconD, iconD);
    Ink();
    dc->DrawTextLayout(D2D1::Point2F(ix + iconD + 16.f, iy + (textH - m.height) / 2), layout.Get(), brush.Get(), D2D1_DRAW_TEXT_OPTIONS_CLIP);

    if (g.yesNo)
    {
        float by = y0 + sh - pad - btnH + 4.f;
        float yesW = std::max(104.f, LabelWidth(g.choices[0]) + 40.f), noW = std::max(96.f, LabelWidth(g.choices[1]) + 40.f);
        Pill(x0 + sw - pad - yesW, by, yesW, btnH, g.choices[0], BtnYes, true);
        Pill(x0 + sw - pad - yesW - 12.f - noW, by, noW, btnH, g.choices[1], BtnNo, false);
    }
    else
    {
        float by = iy + textH + 16.f;
        for (size_t i = 0; i < g.choices.size(); i++, by += btnH + 8.f)
            Pill(x0 + pad, by, sw - 2 * pad, btnH, g.choices[i], BtnChoice + (int)i, i == 0);
    }
}

static void Draw()
{
    if (!dc || !targetBitmap) return;
    g_regions.clear();
    dc->BeginDraw();
    const float inner = WidthDip - 2 * Pad;
    const bool failed = g.finished && g.exitCode != 0;
    const bool ok = g.finished && !failed && !g_cancelled;

    if (g_glass)
    {
        dc->Clear(D2D1::ColorF(0, 0));
        SetColor(255, 255, 255, GlassTint);
        dc->FillRectangle(D2D1::RectF(0, 0, WidthDip, HeightDip), brush.Get());
    }
    else dc->Clear(D2D1::ColorF(244 / 255.f, 245 / 255.f, 247 / 255.f, 1.f));
    DrawBackgroundImage();
    {   // colour wash: indigo at the top-left, rose at the bottom-right, pulled toward green once done
        float done = ok ? EaseOut(Since(g.finishTick, 900.f)) : 0.f;
        auto wash = Linear(0, 0, WidthDip, HeightDip, { { 0.f, Lerp(110, 80, done), Lerp(140, 215, done), Lerp(255, 160, done), g_glass ? 0.20f : 0.12f },
                                                          { 0.5f, 255, 255, 255, 0.0f },
                                                          { 1.f, Lerp(255, 120, done), Lerp(150, 230, done), Lerp(205, 200, done), g_glass ? 0.20f : 0.12f } });
        dc->FillRectangle(D2D1::RectF(0, 0, WidthDip, HeightDip), wash.Get());
        float h = Hair() / 2;
        if (g_glass) SetColor(255, 255, 255, 0.55f); else SetColor(0, 0, 0, 0.12f);
        dc->DrawRoundedRectangle(RR(h, h, WidthDip - h, HeightDip - h, 8.f), brush.Get(), Hair());
    }

    DrawBadge(Pad, 24.f, failed);
    const float tx = Pad + 64.f;
    Ink();
    Text(g.title.empty() ? g.scriptName : g.title, fTitle.Get(), tx, 24, inner - 64 - 90, 30);
    std::wstring sub = g.finished ? (g_cancelled ? L"Cancelled" : failed ? L"Failed (exit code " + std::to_wstring(g.exitCode) + L")" : L"Installation complete")
                                  : g.steps.empty() ? L"Preparing..." : L"Step " + std::to_wstring(g.steps.size());
    if (failed && !g_cancelled) SetColor(255, 69, 58); else if (ok) SetColor(40, 170, 80); else Muted();
    Text(sub, fSub.Get(), tx, 54, inner - 64 - 90, 20);
    if (!g.finished && g.percent >= 0)
    {
        auto grad = Linear(WidthDip - Pad - 110, 0, WidthDip - Pad, 0, { { 0.f, 10, 132, 255, 1.f }, { 1.f, 94, 92, 230, 1.f } });
        dc->DrawText((std::to_wstring((int)lroundf(g_shown)) + L"%").c_str(), (UINT32)(std::to_wstring((int)lroundf(g_shown)).size() + 1), fPercent.Get(),
            D2D1::RectF(WidthDip - Pad - 110, 28, WidthDip - Pad, 68), grad.Get(), D2D1_DRAW_TEXT_OPTIONS_CLIP);
    }

    D2D1_ROUNDED_RECT card = RR(Pad, CardTop, WidthDip - Pad, CardTop + CardH, 14.f);
    Surface(card, CardGlass);
    if (ok)
    {
        float flash = 1.f - Since(g.finishTick, 1000.f);
        if (flash > 0.f) { SetColor(48, 209, 88, 0.16f * flash); dc->FillRoundedRectangle(card, brush.Get()); }
    }
    if (g.details)
    {
        dc->PushAxisAlignedClip(D2D1::RectF(Pad + 14, CardTop + 10, WidthDip - Pad - 14, CardTop + CardH - 10), D2D1_ANTIALIAS_MODE_ALIASED);
        const float line = 18.f;
        int visible = (int)((CardH - 20) / line), total = (int)g.log.size();
        int end = total - g.logScroll, start = std::max(0, end - visible);
        for (int i = start; i < end; i++)
        {
            Muted();
            Text(g.log[i], fMono.Get(), Pad + 14, CardTop + 10 + (i - start) * line, inner - 28, line);
        }
        dc->PopAxisAlignedClip();
    }
    else if (g.steps.empty())
    {
        Muted();
        Text(L"Preparing...", fCenter.Get(), Pad, CardTop, inner, CardH);
    }
    else
    {
        float angle = (float)(GetTickCount64() % 1000) / 1000.f * 6.2831853f;
        int rows = (int)std::min<size_t>(g.steps.size(), MaxVisibleSteps), first = (int)g.steps.size() - rows;
        for (int i = 0; i < rows; i++)
        {
            const Step& s = g.steps[first + i];
            float y = CardTop + 10 + i * RowH;
            if (s.state == StepState::Active)
            {
                auto hi = Linear(Pad + 8, y, WidthDip - Pad - 8, y, { { 0.f, 10, 132, 255, 0.16f }, { 1.f, 191, 90, 242, 0.08f } });
                dc->FillRoundedRectangle(RR(Pad + 8, y, WidthDip - Pad - 8, y + RowH - 2, 9.f), hi.Get());
            }
            DrawStepIcon(s, Pad + 28, y + (RowH - 2) / 2, angle);
            if (s.state == StepState::Done) Muted(); else Ink();
            Text(s.text, fBody.Get(), Pad + 52, y + 7, inner - 70, 22);
        }
    }

    const float bh = 8.f, bw = inner;
    SetColor(0, 0, 0, 0.08f);
    dc->FillRoundedRectangle(RR(Pad, BarY, Pad + bw, BarY + bh, 4.f), brush.Get());
    if (g.percent >= 0 || g.finished)
    {
        float frac = (ok ? 100.f : g_shown) / 100.f;
        float fill = std::max(bh, bw * frac);
        if (frac > 0.f || g.finished)
        {
            D2D1_ROUNDED_RECT bar = RR(Pad, BarY, Pad + fill, BarY + bh, 4.f);
            if (failed) { SetColor(255, 69, 58); dc->FillRoundedRectangle(bar, brush.Get()); }
            else
            {
                auto violet = Linear(Pad, 0, Pad + bw, 0, { { 0.f, 10, 132, 255, 1.f }, { 0.55f, 94, 92, 230, 1.f }, { 1.f, 191, 90, 242, 1.f } });
                dc->FillRoundedRectangle(bar, violet.Get());
                if (ok)
                {
                    auto green = Linear(Pad, 0, Pad + bw, 0, { { 0.f, 52, 199, 89, 1.f }, { 1.f, 100, 210, 255, 1.f } }, EaseOut(Since(g.finishTick, 600.f)));
                    dc->FillRoundedRectangle(bar, green.Get());
                }
            }
        }
    }
    else
    {
        float t = (float)(GetTickCount64() % 1400) / 1400.f, seg = bw * 0.3f;
        float x0 = std::max(Pad + (bw + seg) * t - seg, Pad), x1 = std::min(Pad + (bw + seg) * t, Pad + bw);
        auto grad = Linear(x0, 0, x1 + 1, 0, { { 0.f, 10, 132, 255, 1.f }, { 1.f, 191, 90, 242, 1.f } });
        if (x1 > x0) dc->FillRoundedRectangle(RR(x0, BarY, x1, BarY + bh, 4.f), grad.Get());
    }

    DrawTicker();

    if (g.hover == BtnDetails) Ink(); else Muted();
    Text(g.details ? L"Steps" : L"Details", fSmall.Get(), Pad, FootY + 9, 100, 20);
    g_regions.push_back({ D2D1::RectF(Pad, FootY + 2, Pad + 100, FootY + 36), BtnDetails });
    const bool canLaunch = ok && !g.launchPath.empty();
    Pill(WidthDip - Pad - 104, FootY, 104, 38, g.finished ? L"Close" : L"Cancel", BtnAction, g.finished && !failed && !canLaunch);
    if (canLaunch) Pill(WidthDip - Pad - 104 - 12 - 150, FootY, 150, 38, g.launchLabel.empty() ? L"Launch" : g.launchLabel, BtnLaunch, true);

    if (g.ask) DrawAskSheet();
    dc->EndDraw();
    swap->Present(1, 0);
}

// ---------------------------------------------------------------- state changes
static void AddLog(const std::wstring& line)
{
    g.log.push_back(line);
    g.tickPrev = g.tickCur;
    g.tickCur = Trim(line);
    g.tickTick = GetTickCount64();
    if (g.log.size() > 2000) g.log.pop_front();
}

static void CompleteActive(StepState state)
{
    for (auto& s : g.steps) if (s.state == StepState::Active) s.state = state;
}

// Starts the program named by ".launch" (only after a successful, not cancelled install) and closes the window.
static bool LaunchProgram()
{
    if (!g.finished || g.exitCode != 0 || g_cancelled || g.launchPath.empty()) return false;
    std::wstring dir = g.launchPath;
    size_t slash = dir.find_last_of(L"\\/");
    dir = slash == std::wstring::npos ? L"" : dir.substr(0, slash);
    ShellExecuteW(nullptr, L"open", g.launchPath.c_str(), nullptr, dir.empty() ? nullptr : dir.c_str(), SW_SHOWNORMAL);
    DestroyWindow(g_hwnd);
    return true;
}

static void OnEvent(Event* e)
{
    switch (e->kind)
    {
    case Kind::Title:
        g.title = e->text;
        SetWindowTextW(g_hwnd, (e->text + L" - Setup").c_str());
        break;
    case Kind::Background:
        g.bgPath = e->text;
        break;
    case Kind::Launch:
    {
        size_t bar = e->text.find(L'|');
        g.launchPath = Trim(e->text.substr(0, bar), L" \t\"");
        g.launchLabel = bar == std::wstring::npos ? L"" : Trim(e->text.substr(bar + 1));
        break;
    }
    case Kind::Progress:
        if (int p = ParsePercent(e->text); p >= 0) g.percent = p;
        break;
    case Kind::Info:
        CompleteActive(StepState::Done);
        g.steps.push_back({ StepState::Active, e->text });
        AddLog(L"info   " + e->text);
        break;
    case Kind::Warn:
        g.steps.push_back({ StepState::Warn, e->text });
        AddLog(L"warn   " + e->text);
        break;
    case Kind::Error:
        g.steps.push_back({ StepState::Error, e->text });
        AddLog(L"error  " + e->text);
        break;
    default:
        AddLog((e->fromStderr ? L"stderr " : L"       ") + e->text);
        break;
    }
    Relayout();
    Wake();
    InvalidateRect(g_hwnd, nullptr, FALSE);
}

// index: 1-based option number, 0 = dismissed. For yes/no sheets 1 means yes.
static void AnswerAsk(int index)
{
    g.ask = false;
    if (g.cancelConfirm)
    {
        g.cancelConfirm = false;
        if (index == 1 && !g.finished)
        {
            g_cancelled = true;
            if (g_job) TerminateJobObject(g_job, 2);
        }
    }
    else if (g_pendingAsk)
    {
        g_pendingAsk->answer = index;
        SetEvent(g_pendingAsk->done);
        g_pendingAsk = nullptr;
    }
    g.hover = 0;
    InvalidateRect(g_hwnd, nullptr, FALSE);
}

static void CloseAsk(bool yes) { AnswerAsk(yes ? 1 : (g.yesNo ? 2 : 0)); }

static void RequestCancel()
{
    if (g.ask) return;
    g.ask = true;
    g.askTick = GetTickCount64();
    Wake();
    g.cancelConfirm = true;
    g.yesNo = true;
    g.choices = { L"Yes", L"No" };
    g.askText = L"Cancel setup?";
    InvalidateRect(g_hwnd, nullptr, FALSE);
}

static int HitTest(LPARAM lParam, bool screen)
{
    POINT p{ GET_X_LPARAM(lParam), GET_Y_LPARAM(lParam) };
    if (screen) ScreenToClient(g_hwnd, &p);
    float x = p.x / Scale(), y = p.y / Scale();
    for (auto it = g_regions.rbegin(); it != g_regions.rend(); ++it)
        if (x >= it->rect.left && x < it->rect.right && y >= it->rect.top && y < it->rect.bottom) return it->id;
    return 0;
}

// Frosted glass: Acrylic blur-behind with our own white tint (the system backdrop type paints a fixed grey that
// hides what is behind the window). Tint is ABGR: 0xAABBGGRR, alpha = how milky the glass is.
constexpr DWORD AcrylicTint = 0x59FFFFFF;

static bool ApplyAcrylic()
{
    struct AccentPolicy { int state, flags; DWORD color; int animation; };
    struct CompositionData { int attribute; PVOID data; SIZE_T size; };
    typedef BOOL(WINAPI * SetCompositionFn)(HWND, CompositionData*);
    auto set = (SetCompositionFn)GetProcAddress(GetModuleHandleW(L"user32.dll"), "SetWindowCompositionAttribute");
    if (!set) return false;
    AccentPolicy policy{ 4 /* ACCENT_ENABLE_ACRYLICBLURBEHIND */, 0, AcrylicTint, 0 };
    CompositionData data{ 19 /* WCA_ACCENT_POLICY */, &policy, sizeof policy };
    return set(g_hwnd, &data) != FALSE;
}

static void ApplyDwm()
{
    int corners = 2;   // rounded, anti-aliased by DWM
    DwmSetWindowAttribute(g_hwnd, 33, &corners, sizeof corners);
    MARGINS shadow{ 1, 1, 1, 1 };
    DwmExtendFrameIntoClientArea(g_hwnd, &shadow);   // drop shadow for a borderless window
    if (g_glass && ApplyAcrylic()) return;
    g_glass = false;
}

static LRESULT CALLBACK WndProc(HWND hwnd, UINT msg, WPARAM wParam, LPARAM lParam)
{
    switch (msg)
    {
    case WM_NCCALCSIZE: return 0;
    case WM_NCHITTEST:
    {
        if (g.ask || HitTest(lParam, true)) return HTCLIENT;
        return HTCAPTION;
    }
    case WM_SETCURSOR:
        if (LOWORD(lParam) == HTCLIENT && g.hover) { SetCursor(LoadCursor(nullptr, IDC_HAND)); return TRUE; }
        break;
    case WM_MOUSEMOVE:
    {
        int hot = HitTest(lParam, false);
        if (hot != g.hover) { g.hover = hot; InvalidateRect(hwnd, nullptr, FALSE); }
        TRACKMOUSEEVENT t{ sizeof t, TME_LEAVE, hwnd, 0 };
        TrackMouseEvent(&t);
        return 0;
    }
    case WM_MOUSELEAVE:
        g.hover = 0;
        InvalidateRect(hwnd, nullptr, FALSE);
        return 0;
    case WM_LBUTTONUP:
        switch (HitTest(lParam, false))
        {
        case BtnAction: if (g.finished) DestroyWindow(hwnd); else RequestCancel(); break;
        case BtnLaunch: LaunchProgram(); break;
        case BtnDetails: g.details = !g.details; g.logScroll = 0; Relayout(); InvalidateRect(hwnd, nullptr, FALSE); break;
        case BtnYes: AnswerAsk(1); break;
        case BtnNo: AnswerAsk(2); break;
        default:
            if (int hit = HitTest(lParam, false); hit >= BtnChoice && g.ask) AnswerAsk(hit - BtnChoice + 1);
            break;
        }
        return 0;
    case WM_MOUSEWHEEL:
        if (g.ask && !g.askBody.empty())
        {
            g.licScroll = std::max(0.f, std::min(g.licMax, g.licScroll - GET_WHEEL_DELTA_WPARAM(wParam) / 120.f * 48.f));
            InvalidateRect(hwnd, nullptr, FALSE);
        }
        else if (g.details)
        {
            g.logScroll = std::max(0, std::min((int)g.log.size() - 1, g.logScroll + (GET_WHEEL_DELTA_WPARAM(wParam) > 0 ? 3 : -3)));
            InvalidateRect(hwnd, nullptr, FALSE);
        }
        return 0;
    case WM_KEYDOWN:
        if (wParam == VK_ESCAPE) { if (g.ask) CloseAsk(false); else if (g.finished) DestroyWindow(hwnd); else RequestCancel(); }
        else if (g.ask && !g.askBody.empty())   // license: Enter does not accept, only the button does; arrows scroll
        {
            float step = wParam == VK_DOWN ? 48.f : wParam == VK_UP ? -48.f : wParam == VK_NEXT ? 200.f : wParam == VK_PRIOR ? -200.f : 0.f;
            if (wParam == VK_HOME) g.licScroll = 0.f; else if (wParam == VK_END) g.licScroll = g.licMax;
            g.licScroll = std::max(0.f, std::min(g.licMax, g.licScroll + step));
            InvalidateRect(hwnd, nullptr, FALSE);
        }
        else if (wParam == VK_RETURN) { if (g.ask) CloseAsk(true); else if (g.finished && !LaunchProgram()) DestroyWindow(hwnd); }
        else if (g.ask && !g.yesNo)
        {
            int number = wParam >= '1' && wParam <= '9' ? (int)(wParam - '0') : wParam >= VK_NUMPAD1 && wParam <= VK_NUMPAD9 ? (int)(wParam - VK_NUMPAD0) : 0;
            if (number >= 1 && number <= (int)g.choices.size()) AnswerAsk(number);
        }
        return 0;
    case WM_EVENT:
    {
        std::unique_ptr<Event> e((Event*)lParam);
        OnEvent(e.get());
        return 0;
    }
    case WM_ASK:
        g_pendingAsk = (AskRequest*)lParam;
        g.ask = true;
        g.askTick = GetTickCount64();
        g.cancelConfirm = false;
        g.yesNo = g_pendingAsk->yesNo;
        g.choices = g_pendingAsk->options;
        g.askText = g_pendingAsk->text;
        g.askBody = g_pendingAsk->body;
        g.licScroll = 0.f;
        Wake();
        InvalidateRect(hwnd, nullptr, FALSE);
        return 0;
    case WM_DONE:
    {
        g.exitCode = (int)wParam;
        g.finished = true;
        g.finishTick = GetTickCount64();
        Wake();
        CompleteActive(g.exitCode == 0 ? StepState::Done : StepState::Error);
        if (g.exitCode != 0 && !g_cancelled) g.details = true;
        g.ask = false;
        Relayout();
        InvalidateRect(hwnd, nullptr, FALSE);
        if (g_cancelled) DestroyWindow(hwnd);
        else if (g_autoClose && g.exitCode == 0) SetTimer(hwnd, TimerClose, 800, nullptr);
        return 0;
    }
    case WM_TIMER:
        if (wParam == TimerClose) DestroyWindow(hwnd);
        else
        {
            float target = g.finished ? (g.exitCode == 0 ? 100.f : g_shown) : g.percent < 0 ? 0.f : (float)g.percent;
            g_shown += (target - g_shown) * 0.18f;
            if (fabsf(target - g_shown) < 0.2f) g_shown = target;
            if (!Animating() && g_shown == target) KillTimer(hwnd, TimerAnimate);
            InvalidateRect(hwnd, nullptr, FALSE);
        }
        return 0;
    case WM_DPICHANGED:
    {
        g_dpi = HIWORD(wParam);
        const RECT* r = (const RECT*)lParam;
        SetWindowPos(hwnd, nullptr, r->left, r->top, r->right - r->left, r->bottom - r->top, SWP_NOZORDER | SWP_NOACTIVATE);
        dc->SetTarget(nullptr);
        targetBitmap.Reset();
        RECT c;
        GetClientRect(hwnd, &c);
        swap->ResizeBuffers(0, c.right, c.bottom, DXGI_FORMAT_UNKNOWN, 0);
        CreateTargetBitmap();
        Relayout();
        InvalidateRect(hwnd, nullptr, FALSE);
        return 0;
    }
    case WM_PAINT:
        ValidateRect(hwnd, nullptr);
        Draw();
        return 0;
    case WM_CLOSE:
        if (g.finished) DestroyWindow(hwnd); else RequestCancel();
        return 0;
    case WM_DESTROY:
        PostQuitMessage(0);
        return 0;
    }
    return DefWindowProcW(hwnd, msg, wParam, lParam);
}

// ---------------------------------------------------------------- entry point
static void Console(const std::wstring& text)
{
    HANDLE out = GetStdHandle(STD_OUTPUT_HANDLE);
    if (!out || out == INVALID_HANDLE_VALUE) return;
    std::string s = WideToUtf8(text) + "\n";
    DWORD written;
    WriteFile(out, s.data(), (DWORD)s.size(), &written, nullptr);
}

// ---------------------------------------------------------------- exit code plumbing
// The 7zSD module always exits 0. A packaged setup.exe is therefore [this exe][inner 7zSD installer][offset + "ONESETUP"]:
// started that way, this exe runs the inner installer, which runs `onesetup-ui.exe <script>` in the extraction folder; the
// shell writes the script's exit code to the file named by ONESETUP_RESULT, and the outer exe returns that code.
struct StubFooter { unsigned long long offset; unsigned int metaLength, reserved; char magic[8]; };   // 24 bytes, magic "ONESETU2"

static void WriteResult(int code)
{
    wchar_t path[MAX_PATH * 2];
    DWORD n = GetEnvironmentVariableW(L"ONESETUP_RESULT", path, (DWORD)std::size(path));
    if (n == 0 || n >= std::size(path)) return;
    HANDLE f = CreateFileW(path, GENERIC_WRITE, 0, nullptr, CREATE_ALWAYS, FILE_ATTRIBUTE_NORMAL, nullptr);
    if (f == INVALID_HANDLE_VALUE) return;
    std::string text = std::to_string(code);
    DWORD written;
    WriteFile(f, text.data(), (DWORD)text.size(), &written, nullptr);
    CloseHandle(f);
}

static std::wstring ArgumentsAfterProgram()
{
    const wchar_t* p = GetCommandLineW();
    if (*p == L'"') { ++p; while (*p && *p != L'"') ++p; if (*p) ++p; }
    else while (*p && *p != L' ' && *p != L'\t') ++p;
    while (*p == L' ' || *p == L'\t') ++p;
    return p;
}

struct StubMeta { std::wstring mode, id, entry; };

static StubMeta ParseMeta(const std::string& text)
{
    StubMeta meta;
    size_t start = 0;
    while (start < text.size())
    {
        size_t nl = text.find('\n', start);
        if (nl == std::string::npos) nl = text.size();
        std::string line = text.substr(start, nl - start);
        start = nl + 1;
        size_t eq = line.find('=');
        if (eq == std::string::npos) continue;
        std::wstring key = Utf8ToWide(line.substr(0, eq)), value = Utf8ToWide(line.substr(eq + 1));
        if (key == L"mode") meta.mode = value;
        else if (key == L"id") meta.id = value;
        else if (key == L"entry") meta.entry = value;
    }
    return meta;
}

static bool PathExists(const std::wstring& path) { return GetFileAttributesW(path.c_str()) != INVALID_FILE_ATTRIBUTES; }

static void MakeDirs(const std::wstring& path)
{
    for (size_t i = 3; i <= path.size(); i++)
        if (i == path.size() || path[i] == L'\\')
            CreateDirectoryW(path.substr(0, i).c_str(), nullptr);
}

static void RemoveTree(const std::wstring& dir)
{
    WIN32_FIND_DATAW fd;
    HANDLE h = FindFirstFileW((dir + L"\\*").c_str(), &fd);
    if (h != INVALID_HANDLE_VALUE)
    {
        do
        {
            std::wstring name = fd.cFileName;
            if (name == L"." || name == L"..") continue;
            std::wstring full = dir + L"\\" + name;
            if ((fd.dwFileAttributes & FILE_ATTRIBUTE_DIRECTORY) && !(fd.dwFileAttributes & FILE_ATTRIBUTE_REPARSE_POINT)) RemoveTree(full);
            else
            {
                SetFileAttributesW(full.c_str(), FILE_ATTRIBUTE_NORMAL);
                if (!DeleteFileW(full.c_str())) RemoveDirectoryW(full.c_str());
            }
        } while (FindNextFileW(h, &fd));
        FindClose(h);
    }
    RemoveDirectoryW(dir.c_str());
}

static bool CopyTree(const std::wstring& from, const std::wstring& to)
{
    CreateDirectoryW(to.c_str(), nullptr);
    WIN32_FIND_DATAW fd;
    HANDLE h = FindFirstFileW((from + L"\\*").c_str(), &fd);
    if (h == INVALID_HANDLE_VALUE) return false;
    bool ok = true;
    do
    {
        std::wstring name = fd.cFileName;
        if (name == L"." || name == L"..") continue;
        if (fd.dwFileAttributes & FILE_ATTRIBUTE_DIRECTORY) ok = CopyTree(from + L"\\" + name, to + L"\\" + name) && ok;
        else ok = CopyFileW((from + L"\\" + name).c_str(), (to + L"\\" + name).c_str(), FALSE) && ok;
    } while (ok && FindNextFileW(h, &fd));
    FindClose(h);
    return ok;
}

static bool EndsWithNoCase(const std::wstring& s, const wchar_t* suffix)
{
    size_t n = wcslen(suffix);
    return s.size() >= n && _wcsicmp(s.c_str() + s.size() - n, suffix) == 0;
}

// Starts the packaged program from its cache folder with the caller's arguments, working directory and console.
static int LaunchApp(const std::wstring& appDir, const std::wstring& entry)
{
    SetEnvironmentVariableW(L"ONESETUP_RESULT", nullptr);
    SetEnvironmentVariableW(L"ONESETUP_CACHE_TARGET", nullptr);
    std::wstring path = appDir + L"\\" + entry, args = ArgumentsAfterProgram();
    bool script = EndsWithNoCase(path, L".bat") || EndsWithNoCase(path, L".cmd");
    std::wstring command = script ? L"cmd.exe /d /c call \"" + path + L"\" " + args : L"\"" + path + L"\" " + args;
    SetConsoleCtrlHandler(nullptr, TRUE);   // Ctrl+C belongs to the program, not to this launcher
    STARTUPINFOW si{};
    si.cb = sizeof si;
    PROCESS_INFORMATION pi{};
    if (!CreateProcessW(nullptr, command.data(), nullptr, nullptr, TRUE, 0, nullptr, nullptr, &si, &pi)) return 1;
    WaitForSingleObject(pi.hProcess, INFINITE);
    DWORD code = 1;
    GetExitCodeProcess(pi.hProcess, &code);
    CloseHandle(pi.hProcess);
    CloseHandle(pi.hThread);
    return (int)code;
}

// `onesetup-ui.exe --cache-commit`, run by the inner 7zSD installer inside its extraction folder: moves the extracted files
// into the cache folder (ONESETUP_CACHE_TARGET) via a staging folder and a final rename, so a half-finished or racing
// extraction never leaves a usable-looking cache entry.
static int CacheCommit()
{
    wchar_t buffer[MAX_PATH * 2];
    DWORD n = GetEnvironmentVariableW(L"ONESETUP_CACHE_TARGET", buffer, (DWORD)std::size(buffer));
    if (n == 0 || n >= std::size(buffer)) return 2;
    std::wstring target = buffer;
    GetModuleFileNameW(nullptr, buffer, (DWORD)std::size(buffer));
    std::wstring self = buffer;
    size_t slash = self.find_last_of(L'\\');
    std::wstring source = self.substr(0, slash), selfName = self.substr(slash + 1);
    std::wstring staging = target + L".tmp-" + std::to_wstring(GetCurrentProcessId());
    MakeDirs(staging);
    bool ok = PathExists(staging);
    WIN32_FIND_DATAW fd;
    HANDLE h = ok ? FindFirstFileW((source + L"\\*").c_str(), &fd) : INVALID_HANDLE_VALUE;
    if (h != INVALID_HANDLE_VALUE)
    {
        do
        {
            std::wstring name = fd.cFileName;
            if (name == L"." || name == L".." || _wcsicmp(name.c_str(), selfName.c_str()) == 0) continue;
            std::wstring from = source + L"\\" + name, to = staging + L"\\" + name;
            if (MoveFileExW(from.c_str(), to.c_str(), MOVEFILE_COPY_ALLOWED)) continue;
            if ((fd.dwFileAttributes & FILE_ATTRIBUTE_DIRECTORY) && CopyTree(from, to)) { RemoveTree(from); continue; }
            ok = false;
        } while (ok && FindNextFileW(h, &fd));
        FindClose(h);
    }
    if (ok)
    {
        HANDLE marker = CreateFileW((staging + L"\\.ready").c_str(), GENERIC_WRITE, 0, nullptr, CREATE_ALWAYS, FILE_ATTRIBUTE_NORMAL, nullptr);
        ok = marker != INVALID_HANDLE_VALUE;
        if (ok) CloseHandle(marker);
    }
    if (ok) ok = MoveFileExW(staging.c_str(), target.c_str(), 0) || PathExists(target + L"\\.ready");   // lost a race: the winner's copy is fine
    if (PathExists(staging)) RemoveTree(staging);
    return ok ? 0 : 1;
}

static bool RunAsStub(int& exitCode)
{
    wchar_t self[MAX_PATH * 2];
    GetModuleFileNameW(nullptr, self, (DWORD)std::size(self));
    HANDLE file = CreateFileW(self, GENERIC_READ, FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE, nullptr, OPEN_EXISTING, FILE_ATTRIBUTE_NORMAL, nullptr);
    if (file == INVALID_HANDLE_VALUE) return false;
    LARGE_INTEGER size{}, at{};
    StubFooter footer{};
    DWORD got = 0;
    bool ok = GetFileSizeEx(file, &size) && size.QuadPart > (LONGLONG)sizeof footer;
    if (ok)
    {
        at.QuadPart = size.QuadPart - (LONGLONG)sizeof footer;
        ok = SetFilePointerEx(file, at, nullptr, FILE_BEGIN) && ReadFile(file, &footer, sizeof footer, &got, nullptr) && got == sizeof footer &&
            memcmp(footer.magic, "ONESETU2", 8) == 0 && (LONGLONG)footer.metaLength < at.QuadPart &&
            (LONGLONG)footer.offset < at.QuadPart - (LONGLONG)footer.metaLength;
    }
    if (!ok) { CloseHandle(file); return false; }

    LONGLONG innerEnd = at.QuadPart - (LONGLONG)footer.metaLength;
    std::string metaText(footer.metaLength, '\0');
    LARGE_INTEGER metaAt{};
    metaAt.QuadPart = innerEnd;
    if (footer.metaLength > 0 && !(SetFilePointerEx(file, metaAt, nullptr, FILE_BEGIN) && ReadFile(file, metaText.data(), footer.metaLength, &got, nullptr) && got == footer.metaLength))
    {
        CloseHandle(file);
        exitCode = 1;
        return true;
    }
    StubMeta meta = ParseMeta(metaText);
    bool app = meta.mode == L"app" && !meta.id.empty() && !meta.entry.empty();

    std::wstring appDir;
    if (app)
    {
        wchar_t root[MAX_PATH * 2];
        DWORD n = GetEnvironmentVariableW(L"ONESETUP_CACHE", root, (DWORD)std::size(root));
        std::wstring cacheRoot;
        if (n > 0 && n < std::size(root)) cacheRoot = root;
        else
        {
            n = GetEnvironmentVariableW(L"LOCALAPPDATA", root, (DWORD)std::size(root));
            if (n == 0 || n >= std::size(root)) { CloseHandle(file); exitCode = 1; return true; }
            cacheRoot = std::wstring(root) + L"\\OneSetup\\apps";
        }
        appDir = cacheRoot + L"\\" + meta.id;
        if (PathExists(appDir + L"\\.ready"))   // warm start: nothing to extract
        {
            CloseHandle(file);
            exitCode = LaunchApp(appDir, meta.entry);
            return true;
        }
    }

    wchar_t temp[MAX_PATH];
    GetTempPathW(MAX_PATH, temp);
    std::wstring dir = std::wstring(temp) + L"onesetup-" + std::to_wstring(GetCurrentProcessId()) + L"-" + std::to_wstring(GetTickCount64());
    CreateDirectoryW(dir.c_str(), nullptr);
    std::wstring inner = dir + L"\\setup.exe", result = dir + L"\\result.txt";
    auto cleanup = [&]
    {
        for (int i = 0; i < 30; i++)
        {
            DeleteFileW(inner.c_str());
            DeleteFileW(result.c_str());
            if (RemoveDirectoryW(dir.c_str()) || GetLastError() == ERROR_FILE_NOT_FOUND) break;
            Sleep(100);
        }
    };

    exitCode = 1;
    HANDLE out = CreateFileW(inner.c_str(), GENERIC_WRITE, 0, nullptr, CREATE_NEW, FILE_ATTRIBUTE_NORMAL, nullptr);
    LARGE_INTEGER from{};
    from.QuadPart = (LONGLONG)footer.offset;
    if (out == INVALID_HANDLE_VALUE || !SetFilePointerEx(file, from, nullptr, FILE_BEGIN))
    {
        if (out != INVALID_HANDLE_VALUE) CloseHandle(out);
        CloseHandle(file);
        cleanup();
        return true;
    }
    LONGLONG remaining = innerEnd - (LONGLONG)footer.offset;
    std::vector<char> buffer(1 << 20);
    while (remaining > 0)
    {
        DWORD want = (DWORD)std::min<LONGLONG>(remaining, (LONGLONG)buffer.size()), wrote = 0;
        if (!ReadFile(file, buffer.data(), want, &got, nullptr) || got == 0 || !WriteFile(out, buffer.data(), got, &wrote, nullptr)) break;
        remaining -= got;
    }
    CloseHandle(out);
    CloseHandle(file);
    if (remaining > 0) { cleanup(); return true; }

    SetEnvironmentVariableW(L"ONESETUP_RESULT", result.c_str());
    if (app) SetEnvironmentVariableW(L"ONESETUP_CACHE_TARGET", appDir.c_str());
    std::wstring command = L"\"" + inner + L"\" " + (app ? L"-y" : ArgumentsAfterProgram());
    STARTUPINFOW si{};
    si.cb = sizeof si;
    PROCESS_INFORMATION pi{};
    if (!CreateProcessW(nullptr, command.data(), nullptr, nullptr, FALSE, 0, nullptr, dir.c_str(), &si, &pi)) { cleanup(); return true; }
    WaitForSingleObject(pi.hProcess, INFINITE);
    DWORD innerCode = 1;
    GetExitCodeProcess(pi.hProcess, &innerCode);
    CloseHandle(pi.hProcess);
    CloseHandle(pi.hThread);

    if (app)
    {
        cleanup();   // first run finished: the cache now holds the files; start the program
        exitCode = PathExists(appDir + L"\\.ready") ? LaunchApp(appDir, meta.entry) : 1;
        return true;
    }

    exitCode = (int)innerCode;   // extraction failed or was cancelled: no result file, keep the module's own code
    HANDLE r = CreateFileW(result.c_str(), GENERIC_READ, FILE_SHARE_READ | FILE_SHARE_WRITE, nullptr, OPEN_EXISTING, FILE_ATTRIBUTE_NORMAL, nullptr);
    if (r != INVALID_HANDLE_VALUE)
    {
        char text[32] = {};
        DWORD n = 0;
        if (ReadFile(r, text, sizeof text - 1, &n, nullptr) && n > 0) exitCode = atoi(text);
        CloseHandle(r);
    }
    cleanup();
    return true;
}

// `onesetup-ui.exe --plain install.bat`: run the script in an ordinary console window, as the SFX used to.
static int RunPlain(const std::wstring& script, const std::wstring& extra)
{
    std::wstring command = L"cmd.exe /d /c call \"" + script + L"\" " + extra;
    std::wstring dir = script.substr(0, script.find_last_of(L"\\/"));
    STARTUPINFOW si{};
    si.cb = sizeof si;
    PROCESS_INFORMATION pi{};
    if (!CreateProcessW(nullptr, command.data(), nullptr, nullptr, FALSE, CREATE_NEW_CONSOLE, nullptr, dir.c_str(), &si, &pi)) return 1;
    WaitForSingleObject(pi.hProcess, INFINITE);
    DWORD code = 1;
    GetExitCodeProcess(pi.hProcess, &code);
    CloseHandle(pi.hProcess);
    CloseHandle(pi.hThread);
    return (int)code;
}

int WINAPI wWinMain(HINSTANCE instance, HINSTANCE, PWSTR, int)
{
    {
        int stubCode = 0;
        if (RunAsStub(stubCode)) return stubCode;
    }
    int argc = 0;
    LPWSTR* argv = CommandLineToArgvW(GetCommandLineW(), &argc);
    bool headless = false, noGlass = false, plain = false, cacheCommit = false;
    std::vector<std::wstring> rest;
    for (int i = 1; i < argc; i++)
    {
        std::wstring a = argv[i];
        if (a == L"--headless") headless = true;
        else if (a == L"--auto-close") g_autoClose = true;
        else if (a == L"--no-glass") noGlass = true;
        else if (a == L"--plain") plain = true;
        else if (a == L"--cache-commit") cacheCommit = true;
        else rest.push_back(a);
    }
    if (cacheCommit) return CacheCommit();
    wchar_t env[8];
    if (GetEnvironmentVariableW(L"ONESETUP_UI", env, 8) && wcscmp(env, L"0") == 0) headless = true;
    if (rest.empty())
    {
        MessageBoxW(nullptr, L"usage: onesetup-ui [--headless] [--auto-close] [--no-glass] <script.bat> [arguments]", L"OneSetup", MB_OK);
        return 2;
    }
    wchar_t full[MAX_PATH * 4];
    GetFullPathNameW(rest[0].c_str(), (DWORD)std::size(full), full, nullptr);
    std::wstring script = full, extra;
    for (size_t i = 1; i < rest.size(); i++)
        extra += (i > 1 ? L" " : L"") + (rest[i].find(L' ') != std::wstring::npos ? L"\"" + rest[i] + L"\"" : rest[i]);
    if (GetFileAttributesW(script.c_str()) == INVALID_FILE_ATTRIBUTES)
    {
        if (headless) Console(L"script not found: " + script);
        return 2;
    }
    size_t slash = script.find_last_of(L"\\/"), dot = script.find_last_of(L'.');
    g.scriptName = script.substr(slash + 1, (dot > slash ? dot : script.size()) - slash - 1);

    if (plain)
    {
        int code = RunPlain(script, extra);
        WriteResult(code);
        return code;
    }

    if (headless)
    {
        AttachConsole(ATTACH_PARENT_PROCESS);
        SetEnvironmentVariableW(L"ONESETUP_SHELL", L"headless");
        const wchar_t* names[] = { L"debug", L"info", L"warn", L"error", L"ask", L"progress", L"title", L"choose", L"launch", L"license", L"background" };
        int headlessCode = RunScript(script, extra,
            [&](Kind k, const std::wstring& t, bool err) { Console(L"[" + std::wstring(err ? L"stderr" : names[(int)k]) + L"] " + t); },
            [&](const std::wstring& t, const std::vector<std::wstring>& options, bool yesNo, const std::wstring& body)
            {
                wchar_t a[8];
                if (yesNo)
                {
                    bool yes = !(GetEnvironmentVariableW(L"ONESETUP_ASK", a, 8) && _wcsicmp(a, L"no") == 0);
                    Console((body.empty() ? L"[ask] " : L"[license] ") + t + L" -> " + (yes ? L"yes" : L"no"));
                    return yes ? 1 : 2;
                }
                int pick = GetEnvironmentVariableW(L"ONESETUP_CHOICE", a, 8) ? _wtoi(a) : 1;   // ONESETUP_CHOICE=0 simulates dismissing the sheet
                if (pick < 0 || pick > (int)options.size()) pick = 0;
                std::wstring list;
                for (size_t i = 0; i < options.size(); i++) list += (i ? L" | " : L"") + options[i];
                Console(L"[choose] " + t + L" (" + list + L") -> " + std::to_wstring(pick) + (pick ? L" (" + options[pick - 1] + L")" : L""));
                return pick;
            });
        WriteResult(headlessCode);
        return headlessCode;
    }

    SetEnvironmentVariableW(L"ONESETUP_SHELL", L"ui");
    SetProcessDpiAwarenessContext(DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2);
    WNDCLASSEXW wc{ sizeof wc };
    wc.lpfnWndProc = WndProc;
    wc.hInstance = instance;
    wc.hCursor = LoadCursor(nullptr, IDC_ARROW);
    wc.lpszClassName = L"OneSetupUi";
    // Icon 1 is embedded from assets/onesetup.ico by ui/native/app.rc; it is also the icon of every setup.exe made without --icon.
    wc.hIcon = (HICON)LoadImageW(instance, MAKEINTRESOURCEW(1), IMAGE_ICON, GetSystemMetrics(SM_CXICON), GetSystemMetrics(SM_CYICON), LR_DEFAULTCOLOR);
    wc.hIconSm = (HICON)LoadImageW(instance, MAKEINTRESOURCEW(1), IMAGE_ICON, GetSystemMetrics(SM_CXSMICON), GetSystemMetrics(SM_CYSMICON), LR_DEFAULTCOLOR);
    RegisterClassExW(&wc);

    g_glass = !noGlass;
    g_dpi = GetDpiForSystem();
    int w = (int)lroundf(WidthDip * Scale()), h = (int)lroundf(HeightDip * Scale());
    RECT work;
    SystemParametersInfoW(SPI_GETWORKAREA, 0, &work, 0);
    g_hwnd = CreateWindowExW(WS_EX_NOREDIRECTIONBITMAP | WS_EX_APPWINDOW, wc.lpszClassName, (g.scriptName + L" - Setup").c_str(), WS_POPUP | WS_MINIMIZEBOX,
        work.left + (work.right - work.left - w) / 2, work.top + (work.bottom - work.top - h) / 2 - 40, w, h, nullptr, nullptr, instance, nullptr);
    g_dpi = GetDpiForWindow(g_hwnd);
    ApplyDwm();
    RECT client;
    GetClientRect(g_hwnd, &client);
    if (!InitGraphics(client.right, client.bottom))
    {
        MessageBoxW(nullptr, L"Direct2D is not available; running the script without a window.", L"OneSetup", MB_OK);
        int fallbackCode = RunScript(script, extra, [](Kind, const std::wstring&, bool) {}, [](const std::wstring&, const std::vector<std::wstring>&, bool, const std::wstring&) { return 1; });
        WriteResult(fallbackCode);
        return fallbackCode;
    }
    Relayout();
    ShowWindow(g_hwnd, SW_SHOW);
    {   // An installer window must come to the front and be active: Windows draws Acrylic as a flat colour on inactive windows.
        DWORD foreground = GetWindowThreadProcessId(GetForegroundWindow(), nullptr), self = GetCurrentThreadId();
        if (foreground && foreground != self) AttachThreadInput(self, foreground, TRUE);
        SetForegroundWindow(g_hwnd);
        SetActiveWindow(g_hwnd);
        if (foreground && foreground != self) AttachThreadInput(self, foreground, FALSE);
    }
    SetTimer(g_hwnd, TimerAnimate, 16, nullptr);

    std::thread runner([&]
    {
        int code = RunScript(script, extra,
            [](Kind k, const std::wstring& t, bool err) { PostMessageW(g_hwnd, WM_EVENT, 0, (LPARAM) new Event{ k, t, err }); },
            [](const std::wstring& t, const std::vector<std::wstring>& options, bool yesNo, const std::wstring& body)
            {
                AskRequest request{ t, options, yesNo, body, CreateEventW(nullptr, TRUE, FALSE, nullptr), 0 };
                PostMessageW(g_hwnd, WM_ASK, 0, (LPARAM)&request);
                WaitForSingleObject(request.done, INFINITE);
                CloseHandle(request.done);
                return request.answer;
            });
        PostMessageW(g_hwnd, WM_DONE, (WPARAM)code, 0);
    });

    MSG msg;
    while (GetMessageW(&msg, nullptr, 0, 0) > 0)
    {
        TranslateMessage(&msg);
        DispatchMessageW(&msg);
    }
    if (g_job) TerminateJobObject(g_job, 2);
    runner.join();
    WriteResult(g.exitCode);
    return g.exitCode;
}
