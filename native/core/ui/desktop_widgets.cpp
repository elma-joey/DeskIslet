#include "ui/desktop_widgets.h"

#include "localization.h"

#include "clock.h"
#include "log.h"
#include "motion.h"
#include "settings.h"

#include <dwmapi.h>
#include <imm.h>
#include <shellapi.h>
#include <shlobj.h>
#include <shobjidl.h>
#include <wincodec.h>
#include <windowsx.h>

#include <winrt/Windows.Data.Json.h>
#include <winrt/Windows.Foundation.Collections.h>
#include <winrt/Windows.Foundation.h>

#include <algorithm>
#include <cmath>
#include <map>
#include <random>
#include <set>
#include <thread>

#pragma comment(lib, "dwmapi.lib")

namespace json = winrt::Windows::Data::Json;

namespace atrium {

namespace {

constexpr float kMargin = 24;
constexpr float kGrip = 30; // DIP: the corner that resizes a widget when dragged
constexpr double kGlideMs = 240;
constexpr UINT_PTR kTickTimer = 1, kGlideTimer = 3, kSafetyTimer = 4, kCaretTimer = 5, kSaveTimer = 6;
constexpr UINT kCaretMs = 530, kSaveMs = 800;
constexpr ULONGLONG kPhotoMs = 10 * 60 * 1000; // a new photo every ten minutes
constexpr const wchar_t* kHostClass = L"AtriumWidgetsHost";
constexpr UINT WM_WIDGET_REMOVE = WM_APP + 1;  // lParam Widget*: removed once its own message is done
constexpr UINT WM_WIDGET_RESTACK = WM_APP + 2; // a burst of window events, one restack
constexpr UINT WM_WIDGET_PHOTO = WM_APP + 3;   // lParam PhotoRead*: a photo was read
constexpr UINT WM_WIDGET_WEATHER = WM_APP + 4; // a new forecast (WeatherSource::Latest)
constexpr UINT WM_WIDGET_BLUETOOTH = WM_APP + 5; // the Bluetooth batteries changed (BluetoothBatteries::Latest)

DesktopWidgets* g_widgets = nullptr; // the WinEvent hook's way back

struct KindName {
    WidgetKind kind;
    const wchar_t* name;
};
const KindName kKinds[] = {{WidgetKind::Clock, L"clock"},     {WidgetKind::Calendar, L"calendar"},   {WidgetKind::Battery, L"battery"},
                           {WidgetKind::Digital, L"digital"}, {WidgetKind::System, L"system"},       {WidgetKind::Notes, L"notes"},
                           {WidgetKind::Reminders, L"reminders"}, {WidgetKind::Photos, L"photos"},  {WidgetKind::Weather, L"weather"},
                           {WidgetKind::Bluetooth, L"bluetooth"}, {WidgetKind::Network, L"network"}, {WidgetKind::FileZone, L"file-zone"},
                           {WidgetKind::CodexQuota, L"codex-quota"}};

const wchar_t* NameOf(WidgetKind k)
{
    for (const KindName& n : kKinds)
        if (n.kind == k)
            return n.name;
    return L"clock";
}
bool ParseKind(const std::wstring& s, WidgetKind& k)
{
    for (const KindName& n : kKinds)
        if (s == n.name) {
            k = n.kind;
            return true;
        }
    return false;
}
const wchar_t* SizeName(WidgetSize s) { return s == WidgetSize::Small ? L"small" : s == WidgetSize::Medium ? L"medium" : L"large"; }
WidgetSize ParseSize(const std::wstring& s) { return s == L"large" ? WidgetSize::Large : s == L"medium" ? WidgetSize::Medium : WidgetSize::Small; }
bool Editable(WidgetKind k) { return k == WidgetKind::Notes || k == WidgetKind::Reminders; }
bool Resizable(WidgetKind k) { return WidgetSupports(k, WidgetSize::Medium) || WidgetSupports(k, WidgetSize::Large); }

std::wstring LayoutPath()
{
    wchar_t dir[32768]{};
    if (DWORD n = GetEnvironmentVariableW(L"DESKFOLD_DATA", dir, DWORD(std::size(dir))); n && n < std::size(dir))
        return std::wstring(dir) + L"\\widgets.json";
    std::wstring p = SettingsPath();
    return p.substr(0, p.find_last_of(L'\\')) + L"\\widgets.json";
}

std::wstring ClassOf(HWND h)
{
    wchar_t cls[64] = {};
    GetClassNameW(h, cls, 64);
    return cls;
}

// Without a message to the window (GetWindowTextLength would wait for a hung one).
bool HasTitle(HWND h)
{
    wchar_t t[2] = {};
    return InternalGetWindowText(h, t, 2) > 0;
}

bool IsDesktopWindow(HWND h)
{
    std::wstring c = ClassOf(h);
    return c == L"Progman" || c == L"WorkerW";
}

// The first city of the local time zone's name: "(UTC+08:00) 北京，重庆，…" -> "北京".
std::wstring LocalCity()
{
    DYNAMIC_TIME_ZONE_INFORMATION tz{};
    if (GetDynamicTimeZoneInformation(&tz) == TIME_ZONE_ID_INVALID)
        return {};
    std::wstring key = L"SOFTWARE\\Microsoft\\Windows NT\\CurrentVersion\\Time Zones\\" + std::wstring(tz.TimeZoneKeyName);
    wchar_t display[256] = {};
    DWORD size = sizeof(display);
    if (RegGetValueW(HKEY_LOCAL_MACHINE, key.c_str(), L"Display", RRF_RT_REG_SZ, nullptr, display, &size) != ERROR_SUCCESS)
        return {};
    std::wstring d = display;
    size_t open = d.find(L") ");
    if (open != std::wstring::npos)
        d = d.substr(open + 2);
    size_t end = d.find_first_of(L"，,");
    return end == std::wstring::npos ? d : d.substr(0, end);
}

std::wstring PicturesFolder()
{
    PWSTR path = nullptr;
    std::wstring out;
    if (SUCCEEDED(SHGetKnownFolderPath(FOLDERID_Pictures, 0, nullptr, &path)))
        out = path;
    CoTaskMemFree(path);
    return out;
}

// The pictures of a folder and its subfolders (two levels, a few thousand at most).
void ListPhotos(const std::wstring& dir, std::vector<std::wstring>& out, int depth = 0)
{
    WIN32_FIND_DATAW fd;
    HANDLE h = FindFirstFileW((dir + L"\\*").c_str(), &fd);
    if (h == INVALID_HANDLE_VALUE)
        return;
    do {
        std::wstring name = fd.cFileName;
        if (name == L"." || name == L".." || (fd.dwFileAttributes & (FILE_ATTRIBUTE_HIDDEN | FILE_ATTRIBUTE_SYSTEM)))
            continue;
        if (fd.dwFileAttributes & FILE_ATTRIBUTE_DIRECTORY) {
            if (depth < 2)
                ListPhotos(dir + L"\\" + name, out, depth + 1);
            continue;
        }
        size_t dot = name.find_last_of(L'.');
        if (dot == std::wstring::npos)
            continue;
        std::wstring ext = name.substr(dot);
        std::transform(ext.begin(), ext.end(), ext.begin(), ::towlower);
        for (const wchar_t* e : {L".jpg", L".jpeg", L".png", L".bmp", L".gif", L".webp", L".heic", L".tif", L".tiff"})
            if (ext == e) {
                out.push_back(dir + L"\\" + name);
                break;
            }
    } while (out.size() < 5000 && FindNextFileW(h, &fd));
    FindClose(h);
}

// A photo cropped to fill w x h px (centred) and turned upright, premultiplied BGRA.
IconImage ReadPhoto(const std::wstring& path, UINT w, UINT h)
{
    IconImage out;
    winrt::com_ptr<IWICImagingFactory> f;
    winrt::com_ptr<IWICBitmapDecoder> decoder;
    winrt::com_ptr<IWICBitmapFrameDecode> frame;
    if (FAILED(CoCreateInstance(CLSID_WICImagingFactory, nullptr, CLSCTX_INPROC_SERVER, IID_PPV_ARGS(f.put()))) ||
        FAILED(f->CreateDecoderFromFilename(path.c_str(), nullptr, GENERIC_READ, WICDecodeMetadataCacheOnDemand, decoder.put())) ||
        FAILED(decoder->GetFrame(0, frame.put())))
        return out;
    winrt::com_ptr<IWICBitmapSource> src = frame;
    // The camera's orientation (EXIF): phone pictures are often stored sideways.
    WICBitmapTransformOptions turn = WICBitmapTransformRotate0;
    winrt::com_ptr<IWICMetadataQueryReader> meta;
    if (SUCCEEDED(frame->GetMetadataQueryReader(meta.put()))) {
        PROPVARIANT v;
        PropVariantInit(&v);
        if (SUCCEEDED(meta->GetMetadataByName(L"System.Photo.Orientation", &v)) && v.vt == VT_UI2)
            turn = v.uiVal == 3   ? WICBitmapTransformRotate180
                   : v.uiVal == 6 ? WICBitmapTransformRotate90
                   : v.uiVal == 8 ? WICBitmapTransformRotate270
                                  : WICBitmapTransformRotate0;
        PropVariantClear(&v);
    }
    if (turn != WICBitmapTransformRotate0) {
        winrt::com_ptr<IWICBitmapFlipRotator> rot;
        if (SUCCEEDED(f->CreateBitmapFlipRotator(rot.put())) && SUCCEEDED(rot->Initialize(src.get(), turn)))
            src = rot;
    }
    UINT sw = 0, sh = 0;
    if (FAILED(src->GetSize(&sw, &sh)) || !sw || !sh)
        return out;
    double k = std::max(double(w) / sw, double(h) / sh);
    UINT cw = std::min(sw, UINT(std::lround(w / k))), ch = std::min(sh, UINT(std::lround(h / k)));
    WICRect crop{INT((sw - cw) / 2), INT((sh - ch) / 2), INT(cw), INT(ch)};
    winrt::com_ptr<IWICBitmapClipper> clip;
    winrt::com_ptr<IWICBitmapScaler> scale;
    winrt::com_ptr<IWICFormatConverter> conv;
    if (FAILED(f->CreateBitmapClipper(clip.put())) || FAILED(clip->Initialize(src.get(), &crop)) || FAILED(f->CreateBitmapScaler(scale.put())) ||
        FAILED(scale->Initialize(clip.get(), w, h, WICBitmapInterpolationModeHighQualityCubic)) || FAILED(f->CreateFormatConverter(conv.put())) ||
        FAILED(conv->Initialize(scale.get(), GUID_WICPixelFormat32bppPBGRA, WICBitmapDitherTypeNone, nullptr, 0, WICBitmapPaletteTypeCustom)))
        return out;
    out.bgra.resize(size_t(w) * h * 4);
    if (FAILED(conv->CopyPixels(nullptr, w * 4, UINT(out.bgra.size()), out.bgra.data()))) {
        out.bgra.clear();
        return out;
    }
    out.width = int(w);
    out.height = int(h);
    return out;
}

} // namespace

struct ZoneDropTarget : winrt::implements<ZoneDropTarget, IDropTarget> {
    std::function<bool(const std::wstring&, bool)> accept;
    std::function<void(bool)> highlight;
    DWORD allowed = DROPEFFECT_NONE;
    std::function<std::wstring(POINT)> zoneAt;
    std::wstring zone, paths;
    DWORD sourceEffect = DROPEFFECT_NONE;
    void UpdateZone(POINTL pt) {
        auto next = zoneAt(POINT{pt.x, pt.y});
        if (next == zone) return;
        zone = std::move(next);
        allowed = !zone.empty() && sourceEffect && accept(paths, false) ? sourceEffect : DROPEFFECT_NONE;
    }
    static std::wstring Paths(IDataObject* data)
    {
        if (!data) return {};
        winrt::com_ptr<IShellItemArray> items;
        if (SUCCEEDED(SHCreateShellItemArrayFromDataObject(data, IID_PPV_ARGS(items.put())))) {
            DWORD count = 0;
            std::wstring paths;
            if (FAILED(items->GetCount(&count))) return {};
            for (DWORD i = 0; i < count; ++i) {
                winrt::com_ptr<IShellItem> item;
                PWSTR path = nullptr;
                if (FAILED(items->GetItemAt(i, item.put()))) return {};
                if (FAILED(item->GetDisplayName(SIGDN_FILESYSPATH, &path))) {
                    SFGAOF attributes = 0;
                    if (SUCCEEDED(item->GetAttributes(SFGAO_FILESYSTEM, &attributes)) && !(attributes & SFGAO_FILESYSTEM)) continue;
                    return {};
                }
                if (!paths.empty()) paths += L'\n';
                paths += path;
                CoTaskMemFree(path);
                if (paths.size() > 64000) return {};
            }
            return paths;
        }
        FORMATETC format{CF_HDROP, nullptr, DVASPECT_CONTENT, -1, TYMED_HGLOBAL};
        STGMEDIUM storage{};
        if (FAILED(data->GetData(&format, &storage))) return {};
        HDROP drop = reinterpret_cast<HDROP>(storage.hGlobal);
        std::wstring paths;
        UINT count = DragQueryFileW(drop, 0xffffffff, nullptr, 0);
        for (UINT i = 0; i < count; ++i) {
            std::wstring path(DragQueryFileW(drop, i, nullptr, 0) + 1, L'\0');
            path.resize(DragQueryFileW(drop, i, path.data(), UINT(path.size())));
            if (path.empty() || paths.size() + path.size() + 1 > 64000) { paths.clear(); break; }
            if (i) paths += L'\n';
            paths += path;
        }
        ReleaseStgMedium(&storage);
        return paths;
    }
    HRESULT __stdcall DragEnter(IDataObject* data, DWORD, POINTL pt, DWORD* effect) override {
        allowed = (*effect & DROPEFFECT_LINK) ? DROPEFFECT_LINK : (*effect & DROPEFFECT_COPY);
        if (zoneAt) {
            sourceEffect = allowed; allowed = DROPEFFECT_NONE;
            paths = Paths(data); zone.clear(); UpdateZone(pt);
        } else if (allowed && !accept(Paths(data), false)) allowed = DROPEFFECT_NONE;
        highlight(allowed != DROPEFFECT_NONE);
        *effect = allowed;
        return S_OK;
    }
    HRESULT __stdcall DragOver(DWORD, POINTL pt, DWORD* effect) override { if (zoneAt) UpdateZone(pt); *effect = allowed; return S_OK; }
    HRESULT __stdcall DragLeave() override { highlight(false); allowed = DROPEFFECT_NONE; zone.clear(); paths.clear(); return S_OK; }
    HRESULT __stdcall Drop(IDataObject* data, DWORD, POINTL pt, DWORD* effect) override {
        if (zoneAt) UpdateZone(pt);
        *effect = allowed && accept(Paths(data), true) ? allowed : DROPEFFECT_NONE;
        return DragLeave();
    }
};

struct DesktopWidgets::Widget {
    WidgetKind kind = WidgetKind::Clock;
    WidgetSize size = WidgetSize::Small;
    int col = 0, row = 0;
    std::unique_ptr<Surface> surface;
    winrt::com_ptr<IDropTarget> dropTarget;
    std::wstring revealDrop;
    std::wstring pressedFile;
    std::wstring clickedFile;
    ULONGLONG clickedAt = 0;
    ~Widget() { if (dropTarget && surface) RevokeDragDrop(surface->Hwnd()); }
    bool down = false, dragging = false;
    bool hover = false;    // the mouse is over it: the resize grip shows
    bool tracking = false; // TrackMouseEvent asked for its WM_MOUSELEAVE
    bool resizing = false; // dragged by the grip
    bool growLeft = false; // the selected grip is at the bottom left
    WidgetSize downSize = WidgetSize::Small;
    int downCol = 0;
    POINT downAt{};
    RECT downRect{};
    RECT glideFrom{}, glideTo{};
    double glideMs = 0;
    WidgetContent content;
    std::vector<WidgetHit> hits; // of the last paint
    ULONGLONG photoAt = 0;       // when the photo was last changed
    unsigned photoGen = 0;       // the photo being read
};

namespace {
struct PhotoRead {
    void* widget;
    unsigned gen;
    std::wstring path;
    IconImage image;
};
} // namespace

DesktopWidgets::DesktopWidgets(HINSTANCE instance) : instance_(instance)
{
    wchar_t dir[32768]{}, owner[32]{};
    if (DWORD n = GetEnvironmentVariableW(L"DESKFOLD_DATA", dir, DWORD(std::size(dir))); n && n < std::size(dir))
        dataDirectory_ = dir;
    if (GetEnvironmentVariableW(L"DESKFOLD_OWNER", owner, DWORD(std::size(owner))))
        fileOwner_ = reinterpret_cast<HWND>(_wcstoui64(owner, nullptr, 10));
    if (fileOwner_) zoneOle_ = SUCCEEDED(OleInitialize(nullptr));
    g_widgets = this;
    WNDCLASSEXW wc{sizeof(wc)};
    wc.lpfnWndProc = HostProc;
    wc.hInstance = instance;
    wc.lpszClassName = kHostClass;
    RegisterClassExW(&wc);
    // A hidden top-level window: timers, display changes, the menu's owner.
    host_ = CreateWindowExW(WS_EX_TOOLWINDOW, kHostClass, L"Atrium Widgets", WS_POPUP, 0, 0, 0, 0, nullptr, nullptr, instance, nullptr);
    SetWindowLongPtrW(host_, GWLP_USERDATA, reinterpret_cast<LONG_PTR>(this));
    world_.city = LocalCity();
    world_.lunar = &lunar_;
    weather_ = std::make_unique<WeatherSource>(host_, WM_WIDGET_WEATHER);
    SyncWeatherCity();
    world_.weather = weather_->Latest(); // the last one, until the first fetch
    bluetooth_ = std::make_unique<BluetoothBatteries>(host_, WM_WIDGET_BLUETOOTH);
    network_ = std::make_unique<NetworkMonitor>();
    world_.network = network_.get();
}

void DesktopWidgets::SetMaterial(WidgetMaterial material)
{
    if (material == material_)
        return;
    material_ = material;
    for (auto& w : widgets_)
        if (w->surface) {
            w->surface->SetGlassless(material_ == WidgetMaterial::Color);
            w->surface->Invalidate();
        }
}

void DesktopWidgets::ReloadLanguage()
{
    if (weather_)
        weather_->Refresh();
    for (auto& w : widgets_)
        if (w->surface)
            w->surface->Invalidate();
}

DesktopWidgets::~DesktopWidgets()
{
    if (menuDropWindow_) RevokeDragDrop(menuDropWindow_);
    SetEnabled(false);
    if (zoneOle_) OleUninitialize();
    if (host_)
        DestroyWindow(host_);
    g_widgets = nullptr;
}

void DesktopWidgets::SetEnabled(bool on)
{
    if (on == enabled_)
        return;
    enabled_ = on;
    if (on) {
        if (onStatusRequest)
            onStatusRequest();
        SampleSystem();
        Load();
        Build();
        const DWORD flags = WINEVENT_OUTOFCONTEXT | WINEVENT_SKIPOWNPROCESS;
        hook_ = SetWinEventHook(EVENT_SYSTEM_FOREGROUND, EVENT_SYSTEM_MINIMIZEEND, nullptr, OnWinEvent, 0, 0, flags);
        // A window that appears without taking the foreground, or uncloaks
        // after it did (WinUI), must also send raised widgets back down.
        showHook_ = SetWinEventHook(EVENT_OBJECT_SHOW, EVENT_OBJECT_HIDE, nullptr, OnWinEvent, 0, 0, flags);
        uncloakHook_ = SetWinEventHook(EVENT_OBJECT_UNCLOAKED, EVENT_OBJECT_UNCLOAKED, nullptr, OnWinEvent, 0, 0, flags);
        SetTimer(host_, kTickTimer, 1000, nullptr);
        SetTimer(host_, kSafetyTimer, 3000, nullptr);
        Restack();
        Log(L"widgets: on (%zu)", widgets_.size());
    } else {
        for (HWINEVENTHOOK* h : {&hook_, &showHook_, &uncloakHook_}) {
            if (*h)
                UnhookWinEvent(*h);
            *h = nullptr;
        }
        KillTimer(host_, kTickTimer);
        KillTimer(host_, kSafetyTimer);
        weather_->SetActive(false);
        bluetooth_->SetActive(false);
        network_->SetActive(false);
        KillTimer(host_, kCaretTimer);
        if (KillTimer(host_, kSaveTimer))
            Save(); // typing not saved yet
        Clear();
        Log(L"widgets: off");
    }
}

void DesktopWidgets::SetMonitor(HMONITOR monitor, bool refresh)
{
    if (monitor == monitor_ && !refresh)
        return;
    monitor_ = monitor;
    viewVersion_ = 0;
    if (enabled_)
        for (auto& w : widgets_)
            Place(*w, false);
}

// ---- layout ------------------------------------------------------------------

void DesktopWidgets::SetStatus(const SystemStatus& status)
{
    bool batteryChanged = world_.status.battery != status.battery || world_.status.batteryPercent != status.batteryPercent ||
                          world_.status.charging != status.charging;
    world_.status = status;
    if (status.cpuPercent >= 0)
        world_.cpu = float(status.cpuPercent) / 100.f;
    if (batteryChanged)
        for (auto& widget : widgets_)
            if (widget->kind == WidgetKind::Battery && widget->surface)
                widget->surface->Invalidate();
}

void DesktopWidgets::SetOnRight(bool onRight)
{
    if (onRight == onRight_)
        return;
    onRight_ = onRight;
    if (enabled_)
        for (auto& w : widgets_)
            Place(*w, true);
}

RECT DesktopWidgets::WorkArea() const
{
    MONITORINFO mi{sizeof(mi)};
    HMONITOR m = monitor_ ? monitor_ : MonitorFromPoint(POINT{0, 0}, MONITOR_DEFAULTTOPRIMARY);
    GetMonitorInfoW(m, &mi);
    return mi.rcWork;
}

float DesktopWidgets::Scale() const { return PopupScale(WorkArea()); }

int DesktopWidgets::Cols() const
{
    RECT wa = WorkArea();
    return std::max(1, int((float(wa.right - wa.left) / Scale() - 2 * kMargin + kWidgetGap) / (kWidgetCell + kWidgetGap)));
}

int DesktopWidgets::Rows() const
{
    RECT wa = WorkArea();
    // The bar floats over the bottom of the work area: leave it a row's worth of room.
    return std::max(1, int((float(wa.bottom - wa.top) / Scale() - 2 * kMargin - 90 + kWidgetGap) / (kWidgetCell + kWidgetGap)));
}

// Each half of the grid hugs its own edge (D44): a widget whose middle is left of the grid's
// middle stands kMargin from the left edge, any other kMargin from the right, so whatever the
// grid does not fill is left in the middle and both sides have the same margin.
int DesktopWidgets::VisualCol(int col, WidgetSize size) const { return onRight_ ? Cols() - col - WidgetCols(size) : col; }

bool DesktopWidgets::FromRight(int col, WidgetSize size) const { return 2 * VisualCol(col, size) + WidgetCols(size) >= Cols(); }

RECT DesktopWidgets::CellRect(int col, int row, WidgetSize size) const
{
    RECT wa = WorkArea();
    float s = Scale();
    const float step = kWidgetCell + kWidgetGap;
    const int visualCol = VisualCol(col, size);
    float left = FromRight(col, size) ? float(wa.right - wa.left) / s - kMargin - float(Cols() - visualCol - WidgetCols(size)) * step - WidgetWidth(size)
                                      : kMargin + float(visualCol) * step;
    int x = wa.left + int(std::lround(left * s));
    int y = wa.top + int(std::lround((kMargin + float(row) * (kWidgetCell + kWidgetGap)) * s));
    return RECT{x, y, x + int(std::lround(WidgetWidth(size) * s)), y + int(std::lround(WidgetHeight(size) * s))};
}

bool DesktopWidgets::Fits(int col, int row, WidgetSize size, const Widget* except) const
{
    if (col < 0 || row < 0 || col + WidgetCols(size) > Cols() || row + WidgetRows(size) > Rows())
        return false;
    for (const auto& w : widgets_) {
        if (w.get() == except)
            continue;
        bool apart = col + WidgetCols(size) <= w->col || w->col + WidgetCols(w->size) <= col || row + WidgetRows(size) <= w->row ||
                     w->row + WidgetRows(w->size) <= row;
        if (!apart)
            return false;
    }
    return true;
}

bool DesktopWidgets::FreeCellNear(float col, float row, WidgetSize size, const Widget* except, int& outCol, int& outRow) const
{
    float best = 1e9f;
    for (int r = 0; r < Rows(); ++r)
        for (int c = 0; c < Cols(); ++c) {
            if (!Fits(c, r, size, except))
                continue;
            float d = (float(c) - col) * (float(c) - col) + (float(r) - row) * (float(r) - row);
            if (d < best) {
                best = d;
                outCol = c;
                outRow = r;
            }
        }
    return best < 1e9f;
}

void DesktopWidgets::Load()
{
    quotaIntroduced_ = false;
    quotaVersion_ = nextQuotaRead_ = 0;
    viewVersion_ = 0;
    widgets_.clear();
    unplaced_.clear();
    std::wstring text = ReadTextFile(LayoutPath());
    bool read = false;
    auto add = [&](WidgetKind k, WidgetSize s, int c, int r, WidgetContent content) {
        if (!WidgetSupports(k, s))
            s = WidgetSize::Small;
        if (!Fits(c, r, s, nullptr) && !FreeCellNear(float(c), float(r), s, nullptr, c, r))
            return false; // no room left on this screen
        Create(k, s, c, r).content = std::move(content);
        return true;
    };
    if (!text.empty()) {
        json::JsonObject o{nullptr};
        if (json::JsonObject::TryParse(text, o) && o.HasKey(L"widgets")) {
            read = true;
            quotaIntroduced_ = o.GetNamedBoolean(L"codexQuotaIntroduced", false);
            for (const auto& v : o.GetNamedArray(L"widgets")) {
                if (v.ValueType() != json::JsonValueType::Object)
                    continue;
                json::JsonObject e = v.GetObject();
                WidgetKind k;
                if (!ParseKind(std::wstring(e.GetNamedString(L"kind", L"")), k))
                    continue;
                if (k == WidgetKind::CodexQuota) quotaIntroduced_ = true;
                WidgetContent content;
                content.text = e.GetNamedString(L"text", L"");
                auto layout = e.GetNamedString(L"fileLayout", L"");
                if (layout == L"grid" || layout == L"cards") content.fileLayout = layout;
                content.folder = e.GetNamedString(L"folder", L"");
                if (e.HasKey(L"cities") && e.GetNamedValue(L"cities").ValueType() == json::JsonValueType::Array)
                    for (const auto& cv : e.GetNamedArray(L"cities"))
                        if (cv.ValueType() == json::JsonValueType::String)
                            content.cities.emplace_back(cv.GetString());
                if (e.HasKey(L"items") && e.GetNamedValue(L"items").ValueType() == json::JsonValueType::Array)
                    for (const auto& iv : e.GetNamedArray(L"items")) {
                        if (iv.ValueType() != json::JsonValueType::Object)
                            continue;
                        json::JsonObject io = iv.GetObject();
                        content.items.push_back({std::wstring(io.GetNamedString(L"text", L"")), io.GetNamedBoolean(L"done", false)});
                    }
                if (!add(k, ParseSize(std::wstring(e.GetNamedString(L"size", L"small"))), int(e.GetNamedNumber(L"col", 0)),
                         int(e.GetNamedNumber(L"row", 0)), std::move(content)))
                    unplaced_.emplace_back(e.Stringify());
            }
        } else {
            Log(L"widgets: %s is not valid, starting afresh", LayoutPath().c_str());
        }
    }
    if (!read) { // first run: a column of widgets at the top left, as macOS starts
        add(WidgetKind::Clock, WidgetSize::Small, 0, 0, {});
        add(WidgetKind::Battery, WidgetSize::Small, 1, 0, {});
        add(WidgetKind::Calendar, WidgetSize::Medium, 0, 1, {});
        add(WidgetKind::Clock, WidgetSize::Medium, 0, 2, {});
    }
}

void DesktopWidgets::Save() const
{
    json::JsonArray list;
    for (const auto& w : widgets_) {
        json::JsonObject e;
        e.SetNamedValue(L"kind", json::JsonValue::CreateStringValue(NameOf(w->kind)));
        e.SetNamedValue(L"size", json::JsonValue::CreateStringValue(SizeName(w->size)));
        e.SetNamedValue(L"col", json::JsonValue::CreateNumberValue(w->col));
        e.SetNamedValue(L"row", json::JsonValue::CreateNumberValue(w->row));
        const WidgetContent& c = w->content;
        if (w->kind == WidgetKind::Notes || w->kind == WidgetKind::FileZone)
            e.SetNamedValue(L"text", json::JsonValue::CreateStringValue(c.text));
        if (w->kind == WidgetKind::FileZone && !c.fileLayout.empty())
            e.SetNamedValue(L"fileLayout", json::JsonValue::CreateStringValue(c.fileLayout));
        if (w->kind == WidgetKind::Reminders) {
            json::JsonArray items;
            for (const Reminder& r : c.items) {
                if (r.text.empty())
                    continue; // one being typed
                json::JsonObject io;
                io.SetNamedValue(L"text", json::JsonValue::CreateStringValue(r.text));
                io.SetNamedValue(L"done", json::JsonValue::CreateBooleanValue(r.done));
                items.Append(io);
            }
            e.SetNamedValue(L"items", items);
        }
        if (w->kind == WidgetKind::Photos && !c.folder.empty())
            e.SetNamedValue(L"folder", json::JsonValue::CreateStringValue(c.folder));
        if (!c.cities.empty()) {
            json::JsonArray cities;
            for (const std::wstring& city : c.cities)
                cities.Append(json::JsonValue::CreateStringValue(city));
            e.SetNamedValue(L"cities", cities);
        }
        list.Append(e);
    }
    for (const auto& s : unplaced_)
        if (json::JsonObject e{nullptr}; json::JsonObject::TryParse(s, e))
            list.Append(e);
    json::JsonObject o;
    o.SetNamedValue(L"widgets", list);
    if (!dataDirectory_.empty()) o.SetNamedValue(L"codexQuotaIntroduced", json::JsonValue::CreateBooleanValue(quotaIntroduced_));
    if (!WriteTextFile(LayoutPath(), std::wstring(o.Stringify())))
        Log(L"widgets: could not save the layout");
}

void DesktopWidgets::SaveSoon() { SetTimer(host_, kSaveTimer, kSaveMs, nullptr); }

// ---- windows -----------------------------------------------------------------

DesktopWidgets::Widget& DesktopWidgets::Create(WidgetKind kind, WidgetSize size, int col, int row)
{
    auto w = std::make_unique<Widget>();
    w->kind = kind;
    w->size = size;
    w->col = col;
    w->row = row;
    widgets_.push_back(std::move(w));
    return *widgets_.back();
}

bool DesktopWidgets::Has(const Widget* w) const
{
    return std::any_of(widgets_.begin(), widgets_.end(), [&](const auto& p) { return p.get() == w; });
}

void DesktopWidgets::Build()
{
    for (auto& wp : widgets_) {
        Widget& w = *wp;
        if (w.surface)
            continue;
        // Notes and reminders take the keyboard: their windows can be activated.
        w.surface = std::make_unique<Surface>(instance_, L"Atrium Widget", Editable(w.kind), 14.f, true);
        w.surface->SetRadius(kWidgetRadius);
        w.surface->SetGlassless(material_ == WidgetMaterial::Color); // its own face, no glass under it
        w.surface->paint = [this, &w](Canvas& c) { Paint(w, c); };
        w.surface->message = [this, &w](UINT m, WPARAM wp2, LPARAM lp, LRESULT& r) { return Message(w, m, wp2, lp, r); };
        Place(w, false);
        if (w.kind == WidgetKind::CodexQuota) SetWindowTextW(w.surface->Hwnd(), L"桌屿 · Codex 额度");
        if (w.kind == WidgetKind::FileZone) {
            SetWindowTextW(w.surface->Hwnd(), (L"桌屿 · " + w.content.text).c_str());
            auto target = winrt::make_self<ZoneDropTarget>();
            target->highlight = [&w](bool on) { w.content.dropHover = on; w.surface->Invalidate(); };
            target->accept = [this, &w](const std::wstring& path, bool commit) {
                if (path.empty() || !FileAction(commit ? L"move" : L"canMove", w.content.text, path)) return false;
                if (commit) {
                    auto split = path.find(L'\n');
                    w.revealDrop = path.substr(0, split);
                    w.content.lastDrop = split == std::wstring::npos ? path : L"";
                }
                return true;
            };
            if (SUCCEEDED(RegisterDragDrop(w.surface->Hwnd(), target.get()))) w.dropTarget = target.as<IDropTarget>();
            else Log(L"widgets: unable to register file drop target");
        }
        if (w.kind == WidgetKind::Photos)
            LoadPhoto(w, true);
    }
}

void DesktopWidgets::Clear()
{
    for (auto& w : widgets_)
        if (w->surface)
            w->surface->Hide(false);
    widgets_.clear();
}

void DesktopWidgets::Place(Widget& w, bool glide)
{
    if (!w.surface)
        return;
    RECT to = CellRect(w.col, w.row, w.size);
    if (!w.surface->Visible()) {
        w.surface->Show(to, false, POINT{(to.left + to.right) / 2, (to.top + to.bottom) / 2}, true);
        Restack();
        return;
    }
    if (!glide) {
        w.glideMs = 0;
        w.surface->Move(to);
        return;
    }
    RECT current = w.surface->Rect();
    if (current.right - current.left != to.right - to.left || current.bottom - current.top != to.bottom - to.top)
        w.surface->ReserveResize(D2D1::SizeF(WidgetWidth(WidgetSupports(w.kind, WidgetSize::Medium) ? WidgetSize::Medium : WidgetSize::Small),
                                           WidgetHeight(WidgetSupports(w.kind, WidgetSize::Large) ? WidgetSize::Large : WidgetSize::Small)));
    w.glideFrom = w.surface->Rect();
    w.glideTo = to;
    w.glideMs = ClockMs();
    SetTimer(host_, kGlideTimer, 8, nullptr);
}

void DesktopWidgets::Glide()
{
    bool any = false;
    double now = ClockMs();
    for (auto& w : widgets_) {
        if (w->glideMs <= 0 || !w->surface)
            continue;
        float k = motion::EaseOut(float((now - w->glideMs) / kGlideMs));
        // Size changes too (a widget made larger or smaller).
        auto lerp = [&](LONG a, LONG b) { return LONG(std::lround(float(a) + float(b - a) * k)); };
        RECT r{lerp(w->glideFrom.left, w->glideTo.left), lerp(w->glideFrom.top, w->glideTo.top), lerp(w->glideFrom.right, w->glideTo.right),
               lerp(w->glideFrom.bottom, w->glideTo.bottom)};
        w->surface->Move(r);
        if (k >= 1) {
            w->glideMs = 0;
            if (!w->resizing)
                w->surface->ReserveResize(D2D1::SizeF());
        } else
            any = true;
    }
    if (!any)
        KillTimer(host_, kGlideTimer);
}

bool DesktopWidgets::Add(WidgetKind kind, WidgetSize size)
{
    if (!WidgetSupports(kind, size))
        size = WidgetSize::Small;
    int c = 0, r = 0;
    if (!FreeCellNear(0, 0, size, nullptr, c, r)) {
        Log(L"widgets: no room for another");
        return false;
    }
    Create(kind, size, c, r);
    if (enabled_)
        Build();
    Save();
    Log(L"widgets: added %s %s at %d,%d", NameOf(kind), SizeName(size), c, r);
    return true;
}

bool DesktopWidgets::AddNamed(const std::wstring& kind, const std::wstring& size)
{
    WidgetKind k;
    WidgetSize s;
    return ParseNames(kind, size, k, s) && Add(k, s);
}

bool DesktopWidgets::ParseNames(const std::wstring& kind, const std::wstring& size, WidgetKind& outKind, WidgetSize& outSize)
{
    outSize = ParseSize(size);
    return ParseKind(kind, outKind);
}

void DesktopWidgets::Remove(Widget& w)
{
    // Its window may be handling the message that asked for this.
    PostMessageW(host_, WM_WIDGET_REMOVE, 0, reinterpret_cast<LPARAM>(&w));
}

void DesktopWidgets::RemoveNow(Widget& w)
{
    auto it = std::find_if(widgets_.begin(), widgets_.end(), [&](const auto& p) { return p.get() == &w; });
    if (it == widgets_.end())
        return;
    std::unique_ptr<Widget> gone = std::move(*it);
    widgets_.erase(it);
    viewVersion_ = 0;
    if (gone->surface)
        gone->surface->Hide(false);
    Save();
    Log(L"widgets: removed %s", NameOf(gone->kind));
}

void DesktopWidgets::Resize(Widget& w, WidgetSize size)
{
    if (w.size == size || !WidgetSupports(w.kind, size))
        return;
    int c = w.col, r = w.row;
    if (!Fits(c, r, size, &w) && !FreeCellNear(float(c), float(r), size, &w, c, r))
        return;
    w.size = size;
    viewVersion_ = 0;
    w.col = c;
    w.row = r;
    Place(w, true);
    w.surface->Invalidate();
    if (w.kind == WidgetKind::Photos)
        LoadPhoto(w, false); // the same photo, cropped for the new size
    Save();
}

int DesktopWidgets::GripAt(const Widget& w, POINT screen) const
{
    if (!w.surface || !Resizable(w.kind))
        return 0;
    RECT r = w.surface->Rect();
    int g = int(std::lround(kGrip * Scale()));
    if (screen.y < r.bottom - g || !PtInRect(&r, screen))
        return 0;
    return screen.x < r.left + g ? -1 : screen.x >= r.right - g ? 1 : 0;
}

// Dragging the grip: the size whose corner is nearest the pointer, among those the widget has
// and that fit where it stands; it glides there at once, as the sizes of the menu do.
void DesktopWidgets::DragSize(Widget& w, POINT screen)
{
    float s = Scale();
    const RECT& d = w.downRect; // the edges that stay: the top, and the side away from the grip
    float width = float(w.growLeft ? d.right - screen.x : screen.x - d.left) / s;
    float height = float(screen.y - d.top) / s;
    const float half = kWidgetCell + (kWidgetGap + kWidgetCell) / 2; // between one cell and two
    WidgetSize size = height > half ? WidgetSize::Large : width > half ? WidgetSize::Medium : WidgetSize::Small;
    if (size == WidgetSize::Large && !WidgetSupports(w.kind, size))
        size = WidgetSize::Medium;
    if (!WidgetSupports(w.kind, size))
        size = WidgetSize::Small;
    // The column that keeps the fixed side where it was (near the middle a size may be placed
    // from the other edge, CellRect): within half a cell, or the widget stays as it is.
    int col = -1;
    LONG best = LONG(std::lround((kWidgetCell + kWidgetGap) * s / 2));
    for (int c = std::max(0, w.downCol - 2); c <= w.downCol + 2; ++c) {
        if (!Fits(c, w.row, size, &w))
            continue;
        RECT r = CellRect(c, w.row, size);
        LONG off = std::abs(w.growLeft ? r.right - d.right : r.left - d.left);
        if (off <= best) {
            best = off;
            col = c;
        }
    }
    if (col < 0 || (size == w.size && col == w.col))
        return;
    w.size = size;
    w.col = col;
    Place(w, true);
    w.surface->Invalidate();
}

bool DesktopWidgets::IsOurs(HWND h) const
{
    DWORD pid = 0;
    GetWindowThreadProcessId(h, &pid);
    return pid == GetCurrentProcessId();
}

// ---- the layer -----------------------------------------------------------------

void CALLBACK DesktopWidgets::OnWinEvent(HWINEVENTHOOK, DWORD event, HWND h, LONG obj, LONG child, DWORD, DWORD)
{
    if (!g_widgets || obj != OBJID_WINDOW || child != CHILDID_SELF)
        return;
    // Only top-level show/hide events: child controls change constantly.
    bool appeared = event == EVENT_OBJECT_SHOW || event == EVENT_OBJECT_HIDE || event == EVENT_OBJECT_UNCLOAKED;
    if (appeared && (!h || GetAncestor(h, GA_ROOT) != h))
        return;
    if (!g_widgets->restackPosted_) {
        g_widgets->restackPosted_ = true; // a burst of events, one restack right after it
        PostMessageW(g_widgets->host_, WM_WIDGET_RESTACK, 0, 0);
    }
}

// Always non-topmost, above the desktop when possible and under application windows.
// Clicking or editing never lifts a widget over another application's window.
void DesktopWidgets::Restack()
{
    if (!enabled_ || widgets_.empty())
        return;
    zMoving_ = true;
    struct Done {
        bool& flag;
        ~Done() { flag = false; }
    } done{zMoving_};
    // Find the lowest visible application on this screen.
    struct Scan {
        const DesktopWidgets* self;
        HMONITOR monitor;
        HWND lowest = nullptr;
    } scan{this, monitor_ ? monitor_ : MonitorFromPoint(POINT{0, 0}, MONITOR_DEFAULTTOPRIMARY)};
    EnumWindows(
        [](HWND h, LPARAM l) -> BOOL {
            auto* s = reinterpret_cast<Scan*>(l);
            if (!IsWindowVisible(h) || IsIconic(h) || s->self->IsOurs(h) || IsDesktopWindow(h))
                return TRUE;
            RECT rect{};
            if (!GetWindowRect(h, &rect) || IsRectEmpty(&rect) || (!HasTitle(h) && !(GetWindowLongPtrW(h, GWL_STYLE) & WS_CAPTION)))
                return TRUE;
            BOOL cloaked = FALSE;
            DwmGetWindowAttribute(h, DWMWA_CLOAKED, &cloaked, sizeof(cloaked));
            std::wstring c = ClassOf(h);
            if (cloaked || c == L"Shell_TrayWnd" || c == L"Shell_SecondaryTrayWnd" || c == L"Windows.UI.Core.CoreWindow")
                return TRUE;
            if (MonitorFromWindow(h, MONITOR_DEFAULTTONULL) != s->monitor)
                return TRUE;
            s->lowest = h;
            return TRUE;
        },
        reinterpret_cast<LPARAM>(&scan));
    const UINT flags = SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE | SWP_NOOWNERZORDER;
    // Inserting after a topmost window does not remove WS_EX_TOPMOST.
    // Always leave that band first, including while Explorer is starting.
    for (auto& w : widgets_)
        if (w->surface && (GetWindowLongPtrW(w->surface->Hwnd(), GWL_EXSTYLE) & WS_EX_TOPMOST))
            SetWindowPos(w->surface->Hwnd(), HWND_NOTOPMOST, 0, 0, 0, 0, flags);
    // The window just above the desktop's top window (Progman, or the
    // WorkerW with the icons); the widgets go right under it.
    HWND above = nullptr, h = GetTopWindow(nullptr);
    bool found = false;
    for (; h; h = GetWindow(h, GW_HWNDNEXT)) {
        if (IsWindowVisible(h) && IsDesktopWindow(h)) {
            found = true;
            break;
        }
        bool widget = std::any_of(widgets_.begin(), widgets_.end(), [&](const auto& w) { return w->surface && w->surface->Hwnd() == h; });
        if (!widget)
            above = h;
    }
    if (!found)
        above = nullptr; // no desktop window: keep them at the bottom of the windows
    HWND after = found ? (above ? above : HWND_TOP) : HWND_BOTTOM;
    // Show-desktop and startup can put Progman/WorkerW above an application. Show-desktop
    // (Win+D) also gives the desktop the focus: the widgets come up with it, and stay up
    // while one of them or the bar is clicked. Startup's temporary position is not used
    // to jump over the application.
    HWND fg = GetForegroundWindow();
    const bool desktopInFront = fg && (IsDesktopWindow(fg) || IsOurs(fg));
    if (scan.lowest && !desktopInFront)
        for (HWND below = found ? h : nullptr; below; below = GetWindow(below, GW_HWNDNEXT))
            if (below == scan.lowest) {
                after = scan.lowest;
                break;
            }
    if (after != HWND_TOP && after != HWND_BOTTOM && (GetWindowLongPtrW(after, GWL_EXSTYLE) & WS_EX_TOPMOST))
        after = HWND_TOP; // widgets have already been made non-topmost
    for (auto& w : widgets_) {
        if (!w->surface)
            continue;
        // Every widget is now non-topmost, below applications and above the desktop when possible.
        SetWindowPos(w->surface->Hwnd(), after, 0, 0, 0, 0, flags);
        after = w->surface->Hwnd();
    }
}

LRESULT CALLBACK DesktopWidgets::HostProc(HWND h, UINT m, WPARAM w, LPARAM l)
{
    auto* self = reinterpret_cast<DesktopWidgets*>(GetWindowLongPtrW(h, GWLP_USERDATA));
    if (self) {
        switch (m) {
        case WM_TIMER:
            if (w == kTickTimer) {
                self->Tick();
            } else if (w == kSafetyTimer) {
                self->Restack();
            } else if (w == kGlideTimer) {
                self->Glide();
            } else if (w == kSaveTimer) {
                KillTimer(h, kSaveTimer);
                self->Save();
            } else if (w == kCaretTimer) {
                bool any = false;
                for (auto& wd : self->widgets_)
                    if (wd->content.editing) {
                        wd->content.caretOn = !wd->content.caretOn;
                        wd->surface->Invalidate();
                        any = true;
                    }
                if (!any)
                    KillTimer(h, kCaretTimer);
            }
            return 0;
        case WM_WIDGET_RESTACK:
            self->restackPosted_ = false;
            self->Restack();
            return 0;
        case WM_DISPLAYCHANGE:
        case WM_DPICHANGED:
            if (self->enabled_)
                for (auto& wd : self->widgets_)
                    self->Place(*wd, false);
            return 0;
        case WM_SETTINGCHANGE:
            if (w == SPI_SETWORKAREA && self->enabled_)
                for (auto& wd : self->widgets_)
                    self->Place(*wd, false);
            return 0;
        case WM_WIDGET_REMOVE: {
            auto* gone = reinterpret_cast<Widget*>(l);
            if (self->Has(gone))
                self->RemoveNow(*gone);
            return 0;
        }
        case WM_WIDGET_PHOTO: {
            std::unique_ptr<PhotoRead> read(reinterpret_cast<PhotoRead*>(l));
            auto* wd = static_cast<Widget*>(read->widget);
            if (!self->Has(wd) || read->gen != wd->photoGen || !wd->surface)
                return 0; // removed, or a newer photo is on its way
            WidgetContent& c = wd->content;
            if (!c.imageKey.empty())
                wd->surface->ForgetImage(c.imageKey);
            c.photo = read->path;
            c.image = read->image.empty() ? nullptr : std::make_shared<const IconImage>(std::move(read->image));
            c.imageKey = L"photo:" + std::to_wstring(read->gen);
            wd->photoAt = GetTickCount64();
            wd->surface->Invalidate();
            return 0;
        }
        case WM_WIDGET_WEATHER:
            self->world_.weather = self->weather_->Latest();
            for (auto& wd : self->widgets_)
                if (wd->kind == WidgetKind::Weather && wd->surface)
                    wd->surface->Invalidate();
            return 0;
        case WM_WIDGET_BLUETOOTH:
            self->world_.bluetooth = self->bluetooth_->Latest();
            self->world_.bluetoothRead = self->bluetooth_->Read();
            for (auto& wd : self->widgets_)
                if (wd->kind == WidgetKind::Bluetooth && wd->surface)
                    wd->surface->Invalidate();
            return 0;
        case WM_TIMECHANGE:
            self->world_.city = LocalCity();
            self->Tick();
            return 0;
        }
    }
    return DefWindowProcW(h, m, w, l);
}

bool DesktopWidgets::FileAction(const std::wstring& action, const std::wstring& zone, const std::wstring& path)
{
    if (!fileOwner_ || !IsWindow(fileOwner_)) return false;
    std::wstring payload = action + L"\n" + zone + L"\n" + path;
    COPYDATASTRUCT packet{0x44534631, DWORD(payload.size() * sizeof(wchar_t)), payload.data()};
    DWORD_PTR result = 0;
    return SendMessageTimeoutW(fileOwner_, WM_COPYDATA, reinterpret_cast<WPARAM>(host_), reinterpret_cast<LPARAM>(&packet),
                               SMTO_ABORTIFHUNG | SMTO_BLOCK, 1200, &result) && result;
}

void DesktopWidgets::AttachMenuDrop(HWND hwnd, std::function<std::wstring(POINT)> zoneAt)
{
    if (!zoneOle_ || !hwnd) return;
    auto target = winrt::make_self<ZoneDropTarget>();
    target->zoneAt = std::move(zoneAt);
    target->highlight = [](bool) {};
    target->accept = [this, t = target.get()](const std::wstring& paths, bool commit) {
        return !paths.empty() && FileAction(commit ? L"move" : L"canMove", t->zone, paths);
    };
    if (SUCCEEDED(RegisterDragDrop(hwnd, target.get()))) menuDropWindow_ = hwnd;
}

void DesktopWidgets::DragFile(HWND owner, const std::wstring& path)
{
    winrt::com_ptr<IShellItem> item;
    winrt::com_ptr<IDataObject> data;
    if (SUCCEEDED(SHCreateItemFromParsingName(path.c_str(), nullptr, IID_PPV_ARGS(item.put()))) &&
        SUCCEEDED(item->BindToHandler(nullptr, BHID_DataObject, IID_PPV_ARGS(data.put())))) {
        DWORD effect = 0;
        SHDoDragDrop(owner, data.get(), nullptr, DROPEFFECT_COPY, &effect);
    }
}

std::wstring DesktopWidgets::FileHit(const Widget& w, LPARAM point) const
{
    if (w.kind != WidgetKind::FileZone) return {};
    const float x = GET_X_LPARAM(point) / w.surface->Scale(), y = GET_Y_LPARAM(point) / w.surface->Scale();
    for (const auto& hit : w.hits)
        if (x >= hit.rect.left && x < hit.rect.right && y >= hit.rect.top && y < hit.rect.bottom) return hit.id;
    return {};
}

void DesktopWidgets::SyncFileZones()
{
    if (dataDirectory_.empty()) return;
    const auto path = dataDirectory_ + L"\\view.json";
    WIN32_FILE_ATTRIBUTE_DATA attr{};
    if (!GetFileAttributesExW(path.c_str(), GetFileExInfoStandard, &attr)) return;
    ULARGE_INTEGER stamp{};
    stamp.LowPart = attr.ftLastWriteTime.dwLowDateTime;
    stamp.HighPart = attr.ftLastWriteTime.dwHighDateTime;
    if (stamp.QuadPart == viewVersion_) return;
    json::JsonArray zones{nullptr};
    if (!json::JsonArray::TryParse(ReadTextFile(path), zones)) return;
    std::set<std::wstring> desktopNames, menuNames;
    for (const auto& value : zones) {
        auto obj = value.GetObject();
        (obj.GetNamedBoolean(L"InStartMenu", false) ? menuNames : desktopNames).emplace(obj.GetNamedString(L"Name"));
    }
    std::erase_if(menuZones, [&](const auto& pair) { return !menuNames.contains(pair.first); });
    menuZoneOrder_.clear();
    for (const auto* name : {L"文档", L"文件夹", L"常用入口"})
        if (menuNames.contains(name)) menuZoneOrder_.emplace_back(name);
    for (const auto& name : menuNames)
        if (std::find(menuZoneOrder_.begin(), menuZoneOrder_.end(), name) == menuZoneOrder_.end())
            menuZoneOrder_.push_back(name);
    auto removed = std::erase_if(widgets_, [&](const auto& w) {
        return w->kind == WidgetKind::FileZone && !desktopNames.contains(w->content.text);
    });
    removed += std::erase_if(unplaced_, [&](const std::wstring& saved) {
        json::JsonObject obj{nullptr};
        return json::JsonObject::TryParse(saved, obj) && obj.GetNamedString(L"kind", L"") == L"file-zone" &&
               !desktopNames.contains(std::wstring(obj.GetNamedString(L"text", L"")));
    });
    bool added = false, overflow = false;
    decltype(searchItems_) searchable;
    for (const auto& value : zones) {
        auto zone = value.GetObject();
        std::wstring name(zone.GetNamedString(L"Name"));
        auto it = std::find_if(widgets_.begin(), widgets_.end(), [&](const auto& w) {
            return w->kind == WidgetKind::FileZone && w->content.text == name;
        });
        Widget* widget = nullptr;
        const bool inMenu = menuNames.contains(name);
        if (!inMenu && enabled_ && it == widgets_.end()) {
            int col, row;
            WidgetSize size = WidgetSize::Small;
            // Leave at least one cell per other file zone on a compact display.
            if (Cols() * Rows() >= int(zones.Size()) * 2 + 6 && FreeCellNear(0, 0, WidgetSize::Medium, nullptr, col, row))
                size = WidgetSize::Medium;
            else if (!FreeCellNear(0, 0, size, nullptr, col, row)) col = -1;
            if (col >= 0) {
                widget = &Create(WidgetKind::FileZone, size, col, row);
                widget->content.text = name;
                added = true;
            } else overflow = true;
        } else if (!inMenu && it != widgets_.end()) widget = it->get();
        WidgetContent undisplayed;
        auto& c = inMenu ? menuZones[name] : widget ? widget->content : undisplayed;
        c.text = name;
        std::map<std::wstring, WidgetContent::File> previous;
        for (auto& file : c.files) {
            auto oldPath = file.path;
            previous.emplace(std::move(oldPath), std::move(file));
        }
        c.files.clear();
        for (const auto& v : zone.GetNamedArray(L"Files")) {
            auto f = v.GetObject();
            auto& file = c.files.emplace_back(WidgetContent::File{std::wstring(f.GetNamedString(L"Name")), std::wstring(f.GetNamedString(L"Path")),
                f.GetNamedBoolean(L"Folder", false), f.GetNamedBoolean(L"Collected", false), f.GetNamedBoolean(L"OnDesktop", false),
                std::wstring(f.GetNamedString(L"Modified", L"")), {}});
            auto old = previous.find(file.path);
            if (old != previous.end() && old->second.modified == file.modified) {
                file.icon = std::move(old->second.icon);
                previous.erase(old);
            }
            if (widget && !widget->revealDrop.empty() && f.GetNamedString(L"DropOrigin", L"") == widget->revealDrop) {
                if (!c.lastDrop.empty()) c.lastDrop = file.path;
                widget->revealDrop = file.path;
            }
            searchable.push_back(std::make_shared<ZoneSearchItem>(ZoneSearchItem{name, file}));
        }
        if (!widget) continue;
        if (widget->surface) for (const auto& old : previous) widget->surface->ForgetImage(old.first);
        if (!c.lastDrop.empty() && std::none_of(c.files.begin(), c.files.end(), [&](const auto& f) { return f.path == c.lastDrop; })) c.lastDrop.clear();
        const auto layout = LayoutFileZone(c, widget->size);
        if (!widget->revealDrop.empty()) {
            auto found = std::find_if(c.files.begin(), c.files.end(), [&](const auto& f) { return _wcsicmp(f.path.c_str(), widget->revealDrop.c_str()) == 0; });
            if (found != c.files.end()) {
                c.scroll = layout.RowOffset(size_t(found - c.files.begin()));
                widget->revealDrop.clear();
            }
        }
        c.scroll = std::clamp(c.scroll, 0.f, layout.maxScroll);
        if (widget->surface) widget->surface->Invalidate();
    }
    searchItems_ = std::move(searchable);
    if (enabled_ && !quotaIntroduced_ && !overflow) AddQuota();
    if (enabled_ && (added || removed)) { Build(); Save(); Restack(); }
    if (!viewVersion_) FileAction(overflow ? L"overflow" : L"ready");
    viewVersion_ = stamp.QuadPart;
}

bool DesktopWidgets::AddQuota()
{
    for (const auto& w : widgets_) if (w->kind == WidgetKind::CodexQuota) return true;
    int col, row;
    WidgetSize size = WidgetSize::Medium;
    if (!FreeCellNear(float(Cols() - 1), 0, size, nullptr, col, row)) {
        size = WidgetSize::Small;
        if (!FreeCellNear(float(Cols() - 1), 0, size, nullptr, col, row)) return false;
    }
    Create(WidgetKind::CodexQuota, size, col, row);
    std::erase_if(unplaced_, [](const std::wstring& saved) {
        json::JsonObject entry{nullptr};
        return json::JsonObject::TryParse(saved, entry) && entry.GetNamedString(L"kind", L"") == L"codex-quota";
    });
    quotaIntroduced_ = true;
    nextQuotaRead_ = quotaVersion_ = 0;
    Build(); Save(); Restack();
    return true;
}

void DesktopWidgets::SyncQuota()
{
    if (dataDirectory_.empty()) return;
    auto it = std::find_if(widgets_.begin(), widgets_.end(), [](const auto& w) { return w->kind == WidgetKind::CodexQuota; });
    if (it == widgets_.end()) return;
    auto& w = **it;
    const auto path = dataDirectory_ + L"\\codex-quota.json";
    WIN32_FILE_ATTRIBUTE_DATA attr{};
    ULARGE_INTEGER stamp{};
    if (GetFileAttributesExW(path.c_str(), GetFileExInfoStandard, &attr)) {
        stamp.LowPart = attr.ftLastWriteTime.dwLowDateTime;
        stamp.HighPart = attr.ftLastWriteTime.dwHighDateTime;
    }
    // On startup wait for this session's account check before displaying any previous snapshot.
    if (!nextQuotaRead_) quotaVersion_ = stamp.QuadPart;
    const auto now = GetTickCount64();
    if (now >= nextQuotaRead_)
        nextQuotaRead_ = now + (FileAction(L"quotaRefresh") && !w.content.quotaStatus.empty() ? 300000 : 10000);
    if (!stamp.QuadPart || stamp.QuadPart == quotaVersion_) return;
    json::JsonObject data{nullptr};
    if (!json::JsonObject::TryParse(ReadTextFile(path), data)) return;
    try {
        std::vector<WidgetContent::Limit> limits;
        for (const auto& item : data.GetNamedArray(L"Windows")) {
            auto row = item.GetObject();
            auto remaining = row.GetNamedNumber(L"Remaining");
            if (remaining >= 0 && remaining <= 100 && limits.size() < 2)
                limits.push_back({std::wstring(row.GetNamedString(L"Label")), std::wstring(row.GetNamedString(L"Reset", L"")), remaining});
        }
        w.content.quotaStatus = data.GetNamedString(L"Status", L"同步失败");
        w.content.quotaUpdated = data.HasKey(L"Updated") && data.GetNamedValue(L"Updated").ValueType() == json::JsonValueType::String ?
            std::wstring(data.GetNamedString(L"Updated")) : L"";
        w.content.limits = std::move(limits);
        quotaVersion_ = stamp.QuadPart;
        nextQuotaRead_ = now + 300000;
        if (w.surface) {
            std::wstring title = L"桌屿 · Codex 额度 · " + w.content.quotaStatus;
            for (const auto& limit : w.content.limits) {
                wchar_t remain[20]{};
                swprintf_s(remain, L" %.0f%%", limit.remaining);
                title += L" · " + limit.label + L"剩余" + remain;
                if (!limit.reset.empty()) title += L"，重置 " + limit.reset;
            }
            SetWindowTextW(w.surface->Hwnd(), title.c_str());
            w.surface->Invalidate();
        }
    } catch (const winrt::hresult_error&) { /* Retry an incomplete snapshot on the next tick. */ }
}

void DesktopWidgets::SyncWeatherCity()
{
    if (dataDirectory_.empty()) return;
    auto path = dataDirectory_ + L"\\weather-city.json";
    WIN32_FILE_ATTRIBUTE_DATA attr{};
    if (!GetFileAttributesExW(path.c_str(), GetFileExInfoStandard, &attr)) return;
    ULARGE_INTEGER stamp{};
    stamp.HighPart = attr.ftLastWriteTime.dwHighDateTime;
    stamp.LowPart = attr.ftLastWriteTime.dwLowDateTime;
    if (weatherCityVersion_ == stamp.QuadPart) return;
    json::JsonObject selection{nullptr};
    try {
        if (!json::JsonObject::TryParse(ReadTextFile(path), selection)) return;
        auto city = std::wstring(selection.GetNamedString(L"City"));
        weather_->SetLocation(city, selection.GetNamedNumber(L"Latitude"), selection.GetNamedNumber(L"Longitude"),
                              dataDirectory_ + L"\\weather-manual-cache.json");
        weatherCityVersion_ = stamp.QuadPart;
    } catch (const winrt::hresult_error&) { return; }
}

void DesktopWidgets::Tick()
{
    SyncWeatherCity();
    SyncFileZones();
    SyncQuota();
    SYSTEMTIME t;
    GetLocalTime(&t);
    bool minute = t.wMinute != lastMinute_;
    lastMinute_ = t.wMinute;
    if ((minute || t.wSecond % 30 == 0) && onStatusRequest)
        onStatusRequest();
    bool sample = t.wSecond % 2 == 0;
    if (sample)
        SampleSystem();
    ULONGLONG now = GetTickCount64();
    // The forecast is fetched while a weather widget is on the desktop.
    weather_->SetActive(std::any_of(widgets_.begin(), widgets_.end(), [](const auto& w) { return w->kind == WidgetKind::Weather; }));
    // The Bluetooth batteries are read while a Bluetooth widget is.
    bluetooth_->SetActive(std::any_of(widgets_.begin(), widgets_.end(), [](const auto& w) { return w->kind == WidgetKind::Bluetooth; }));
    // The link's rates and the minute of them behind them, once a second while a network widget is up.
    const bool watching = std::any_of(widgets_.begin(), widgets_.end(), [](const auto& w) { return w->kind == WidgetKind::Network; });
    network_->SetActive(watching);
    if (watching)
        network_->Sample();
    for (auto& w : widgets_) {
        if (!w->surface)
            continue;
        if (w->content.awaitingFileIcons || w->kind == WidgetKind::Clock || minute || (sample && w->kind == WidgetKind::System) || (watching && w->kind == WidgetKind::Network))
            w->surface->Invalidate();
        if (w->kind == WidgetKind::Photos && w->photoAt && now - w->photoAt > kPhotoMs)
            LoadPhoto(*w, true);
    }
}

void DesktopWidgets::SampleSystem()
{
    MEMORYSTATUSEX ms{sizeof(ms)};
    if (GlobalMemoryStatusEx(&ms)) {
        constexpr double kGb = 1024.0 * 1024 * 1024;
        world_.memory = float(ms.dwMemoryLoad) / 100.f;
        world_.memoryTotalGb = double(ms.ullTotalPhys) / kGb;
        world_.memoryGb = double(ms.ullTotalPhys - ms.ullAvailPhys) / kGb;
    }
    wchar_t windows[MAX_PATH] = {};
    GetWindowsDirectoryW(windows, MAX_PATH);
    std::wstring root = std::wstring(windows).substr(0, 3); // "C:\"
    ULARGE_INTEGER avail{}, total{}, free{};
    if (GetDiskFreeSpaceExW(root.c_str(), &avail, &total, &free) && total.QuadPart) {
        world_.disk = 1.f - float(double(free.QuadPart) / double(total.QuadPart));
        world_.diskFreeGb = double(avail.QuadPart) / (1024.0 * 1024 * 1024);
    }
}

// ---- photos ------------------------------------------------------------------

void DesktopWidgets::PickFolder(Widget& w)
{
    winrt::com_ptr<IFileOpenDialog> dialog;
    if (FAILED(CoCreateInstance(CLSID_FileOpenDialog, nullptr, CLSCTX_INPROC_SERVER, IID_PPV_ARGS(dialog.put()))))
        return;
    FILEOPENDIALOGOPTIONS o = 0;
    dialog->GetOptions(&o);
    dialog->SetOptions(o | FOS_PICKFOLDERS | FOS_FORCEFILESYSTEM);
    dialog->SetTitle(Tr(L"Choose a photo folder"));
    Widget* wp = &w;
    winrt::com_ptr<IShellItem> item;
    PWSTR path = nullptr;
    if (FAILED(dialog->Show(host_)) || FAILED(dialog->GetResult(item.put())) || FAILED(item->GetDisplayName(SIGDN_FILESYSPATH, &path)))
        return;
    if (Has(wp)) { // the dialog ran its own message loop
        wp->content.folder = path;
        Save();
        LoadPhoto(*wp, true);
        Log(L"widgets: photos from a chosen folder");
    }
    CoTaskMemFree(path);
}

// another: a different picture of the folder; else the one shown, read again (a new size).
void DesktopWidgets::LoadPhoto(Widget& w, bool another)
{
    if (!w.surface)
        return;
    std::wstring folder = w.content.folder.empty() ? PicturesFolder() : w.content.folder;
    std::wstring current = w.content.photo;
    float s = Scale();
    UINT pw = UINT(std::lround(WidgetWidth(w.size) * s)), ph = UINT(std::lround(WidgetHeight(w.size) * s));
    unsigned gen = w.photoGen = ++photoGen_;
    w.photoAt = GetTickCount64(); // no second request while this one runs
    HWND host = host_;
    void* widget = &w;
    std::thread([=] {
        CoInitializeEx(nullptr, COINIT_MULTITHREADED);
        std::wstring path = current;
        if (another || path.empty()) {
            std::vector<std::wstring> all;
            ListPhotos(folder, all);
            if (all.size() > 1)
                all.erase(std::remove(all.begin(), all.end(), current), all.end());
            path.clear();
            if (!all.empty()) {
                std::mt19937 rng{std::random_device{}()};
                path = all[std::uniform_int_distribution<size_t>(0, all.size() - 1)(rng)];
            }
        }
        auto* read = new PhotoRead{widget, gen, path, path.empty() ? IconImage{} : ReadPhoto(path, pw, ph)};
        CoUninitialize();
        if (!PostMessageW(host, WM_WIDGET_PHOTO, 0, reinterpret_cast<LPARAM>(read)))
            delete read;
    }).detach();
}

// ---- input -------------------------------------------------------------------

bool DesktopWidgets::Message(Widget& w, UINT m, WPARAM wp, LPARAM lp, LRESULT& r)
{
    if (shellMenuMessage_ && (m == WM_INITMENUPOPUP || m == WM_DRAWITEM || m == WM_MEASUREITEM || m == WM_MENUCHAR))
        return shellMenuMessage_(m, wp, lp, r);
    switch (m) {
    case WM_WINDOWPOSCHANGING: {
        // Activation still gives editable widgets keyboard focus, without changing their layer.
        auto* pos = reinterpret_cast<WINDOWPOS*>(lp);
        if (!zMoving_)
            pos->flags |= SWP_NOZORDER;
        return false;
    }
    case WM_MOUSEWHEEL:
        if (w.kind == WidgetKind::FileZone) {
            const auto layout = LayoutFileZone(w.content, w.size);
            w.content.scroll = std::clamp(std::min(w.content.scroll, layout.maxScroll) - float(GET_WHEEL_DELTA_WPARAM(wp)) / WHEEL_DELTA * layout.rowPitch,
                                          0.f, layout.maxScroll);
            w.content.hoveredFile.clear();
            w.surface->Invalidate();
            return true;
        }
        if (w.kind != WidgetKind::Reminders)
            return false;
        w.content.scroll = std::clamp(w.content.scroll - float(GET_WHEEL_DELTA_WPARAM(wp)) / WHEEL_DELTA * kReminderRow * 3,
                                      0.f, ReminderScrollMax(w.content, w.size));
        w.surface->Invalidate();
        return true;
    case WM_SETCURSOR: {
        POINT p;
        GetCursorPos(&p);
        int grip = w.resizing ? (w.growLeft ? -1 : 1) : w.down ? 0 : GripAt(w, p);
        if (LOWORD(lp) != HTCLIENT || !grip)
            return false;
        SetCursor(LoadCursorW(nullptr, grip < 0 ? IDC_SIZENESW : IDC_SIZENWSE));
        r = TRUE;
        return true;
    }
    case WM_MOUSELEAVE:
        w.tracking = false;
        w.content.hoveredFile.clear();
        if (w.hover && !w.down) {
            w.hover = false;
            w.surface->Invalidate();
        }
        return true;
    case WM_LBUTTONDOWN: {
        w.down = true;
        w.dragging = false;
        GetCursorPos(&w.downAt);
        w.downRect = w.surface->Rect();
        int grip = GripAt(w, w.downAt);
        w.growLeft = grip < 0;
        w.resizing = grip != 0;
        w.pressedFile = w.resizing ? L"" : FileHit(w, lp);
        w.downSize = w.size;
        w.downCol = w.col;
        if (w.resizing) {
            EndEdit(w);
            w.surface->ReserveResize(D2D1::SizeF(WidgetWidth(WidgetSupports(w.kind, WidgetSize::Medium) ? WidgetSize::Medium : WidgetSize::Small),
                                                WidgetHeight(WidgetSupports(w.kind, WidgetSize::Large) ? WidgetSize::Large : WidgetSize::Small)));
        }
        SetCapture(w.surface->Hwnd());
        return true;
    }
    case WM_MOUSEMOVE: {
        if (w.kind == WidgetKind::FileZone && !w.down) {
            auto file = FileHit(w, lp);
            if (w.content.hoveredFile != file) {
                w.content.hoveredFile = std::move(file);
                w.surface->Invalidate();
            }
        }
        if (!w.tracking) {
            TRACKMOUSEEVENT t{sizeof(t), TME_LEAVE, w.surface->Hwnd(), 0};
            w.tracking = TrackMouseEvent(&t) != FALSE;
        }
        if (!w.hover) {
            w.hover = true;
            w.surface->Invalidate();
        }
        if (!w.down)
            return true;
        POINT p;
        GetCursorPos(&p);
        if (w.resizing) {
            DragSize(w, p);
            return true;
        }
        int dx = p.x - w.downAt.x, dy = p.y - w.downAt.y;
        if (!w.dragging && std::abs(dx) + std::abs(dy) > int(6 * Scale())) {
            if (!w.pressedFile.empty()) {
                const auto path = std::exchange(w.pressedFile, {});
                w.down = false;
                ReleaseCapture();
                if (path == L"learn") return true;
                DragFile(w.surface->Hwnd(), path);
                return true;
            }
            w.dragging = true;
            w.glideMs = 0;
            EndEdit(w);
            Restack(); // dragging stays on the desktop, below open application windows
        }
        if (w.dragging)
            w.surface->Move(RECT{w.downRect.left + dx, w.downRect.top + dy, w.downRect.right + dx, w.downRect.bottom + dy});
        return true;
    }
    case WM_LBUTTONUP:
    case WM_CAPTURECHANGED: {
        if (!w.down)
            return m == WM_LBUTTONUP;
        w.down = false;
        bool dragged = w.dragging, resized = w.resizing;
        w.dragging = w.resizing = false;
        if (m == WM_LBUTTONUP && GetCapture() == w.surface->Hwnd())
            ReleaseCapture();
        POINT at;
        GetCursorPos(&at);
        if (RECT now = w.surface->Rect(); w.hover && !PtInRect(&now, at)) {
            w.hover = false; // let go outside it (its WM_MOUSELEAVE came while it was held)
            w.surface->Invalidate();
        }
        if (resized) {
            if (w.glideMs <= 0)
                w.surface->ReserveResize(D2D1::SizeF());
            if (w.size != w.downSize) {
                viewVersion_ = 0;
                if (w.kind == WidgetKind::Photos)
                    LoadPhoto(w, false); // the same photo, cropped for the new size
                Save();
                Log(L"widgets: %s resized to %s at %d,%d", NameOf(w.kind), SizeName(w.size), w.col, w.row);
            }
        } else if (dragged) {
            // Snaps to the nearest free place of the grid and glides there.
            RECT cur = w.surface->Rect(), wa = WorkArea();
            float s = Scale();
            // Counted from the edge the widget was dropped nearer to, as CellRect places it.
            float col = (float(cur.left - wa.left) / s - kMargin) / (kWidgetCell + kWidgetGap);
            if (cur.left + cur.right > wa.left + wa.right)
                col = float(Cols() - WidgetCols(w.size)) - (float(wa.right - cur.right) / s - kMargin) / (kWidgetCell + kWidgetGap);
            if (onRight_)
                col = float(Cols() - WidgetCols(w.size)) - col;
            float row = (float(cur.top - wa.top) / s - kMargin) / (kWidgetCell + kWidgetGap);
            int c = w.col, rr = w.row;
            if (FreeCellNear(col, row, w.size, &w, c, rr)) {
                w.col = c;
                w.row = rr;
            }
            Place(w, true);
            Save();
            Restack();
            Log(L"widgets: %s moved to %d,%d", NameOf(w.kind), w.col, w.row);
        } else if (m == WM_LBUTTONUP) {
            if (w.kind == WidgetKind::FileZone) {
                if (w.pressedFile == L"learn" && FileAction(L"learn", w.content.text, w.content.lastDrop)) {
                    w.content.lastDrop.clear(); w.surface->Invalidate();
                } else if (!w.pressedFile.empty() && w.pressedFile != L"learn") {
                    const auto now = GetTickCount64();
                    if (w.clickedFile == w.pressedFile && now - w.clickedAt <= GetDoubleClickTime()) {
                        FileAction(L"open", w.content.text, w.pressedFile);
                        w.clickedFile.clear();
                    } else w.clickedFile = w.pressedFile;
                    w.clickedAt = now;
                }
            } else if (Editable(w.kind))
                Click(w, POINT{GET_X_LPARAM(lp), GET_Y_LPARAM(lp)});
            else
                Activate(w);
        }
        return true;
    }
    case WM_RBUTTONUP:
        Menu(w, FileHit(w, lp));
        return true;
    case WM_LBUTTONDBLCLK:
    case WM_RBUTTONDOWN:
        return true;
    case WM_CHAR:
    case WM_KEYDOWN:
        return w.content.editing && Key(w, m, wp);
    case WM_KILLFOCUS:
        EndEdit(w);
        return false;
    case WM_IME_STARTCOMPOSITION:
        PlaceImeWindow(w);
        return false; // the input method's own window
    }
    return false;
}

void DesktopWidgets::Activate(Widget& w)
{
    POINT p;
    GetCursorPos(&p);
    POINT c = w.surface->ScreenToContent(p);
    float s = w.surface->Scale();
    w.surface->Ripple(float(c.x) / s, float(c.y) / s);
    std::wstring open;
    switch (w.kind) {
    case WidgetKind::CodexQuota:
        FileAction(L"quotaRefresh");
        break;
    case WidgetKind::Clock:
    case WidgetKind::Digital:
        open = L"ms-clock:";
        break;
    case WidgetKind::Calendar:
        if (onCalendar)
            onCalendar();
        break;
    case WidgetKind::Battery:
        open = world_.status.battery ? L"ms-settings:batterysaver" : L"ms-settings:powersleep";
        break;
    case WidgetKind::System:
        open = L"taskmgr.exe";
        break;
    case WidgetKind::Bluetooth:
        open = L"ms-settings:bluetooth"; // Bluetooth & devices: each device with its battery
        break;
    case WidgetKind::Network:
        open = L"ms-settings:network-status"; // 网络和 Internet: the adapters, their speeds and their usage
        break;
    case WidgetKind::Photos:
        if (w.content.photo.empty())
            PickFolder(w);
        else
            open = w.content.photo;
        break;
    case WidgetKind::Weather:
        // Windows' weather app, else its web page.
        if (INT_PTR(ShellExecuteW(nullptr, L"open", L"msnweather:", nullptr, nullptr, SW_SHOWNORMAL)) <= 32)
            open = L"https://www.msn.cn/zh-cn/weather/forecast";
        break;
    default:
        break;
    }
    if (!open.empty())
        ShellExecuteW(nullptr, L"open", open.c_str(), nullptr, nullptr, SW_SHOWNORMAL);
    Log(L"widgets: %s opened", NameOf(w.kind));
}

void DesktopWidgets::Click(Widget& w, POINT content)
{
    if (w.kind == WidgetKind::Notes) {
        StartEdit(w, -1);
        return;
    }
    float s = w.surface->Scale();
    D2D1_POINT_2F p = D2D1::Point2F(float(content.x) / s, float(content.y) / s);
    std::wstring id;
    for (const WidgetHit& h : w.hits)
        if (h.id != L"caret" && p.x >= h.rect.left && p.x < h.rect.right && p.y >= h.rect.top && p.y < h.rect.bottom) {
            id = h.id;
            break;
        }
    WidgetContent& c = w.content;
    if (id.rfind(L"toggle:", 0) == 0) {
        size_t i = size_t(_wtoi(id.c_str() + 7));
        if (c.editing && c.item == int(i))
            EndEdit(w);
        if (i < c.items.size() && !c.items[i].text.empty()) {
            c.items[i].done = !c.items[i].done;
            SaveSoon();
        }
        w.surface->Invalidate();
    } else if (id.rfind(L"item:", 0) == 0) {
        StartEdit(w, _wtoi(id.c_str() + 5));
    } else if (id == L"add") {
        EndEdit(w);
        c.items.push_back({});
        StartEdit(w, int(c.items.size()) - 1);
    } else {
        EndEdit(w);
    }
}

void DesktopWidgets::StartEdit(Widget& w, int item)
{
    WidgetContent& c = w.content;
    if (c.editing && c.item != item) {
        // Another reminder: an empty one being left goes, which may move this one up.
        if (w.kind == WidgetKind::Reminders && c.item >= 0 && c.item < item && c.items[size_t(c.item)].text.empty())
            --item;
        EndEdit(w);
    }
    if (w.kind == WidgetKind::Reminders && (item < 0 || item >= int(c.items.size())))
        return;
    c.editing = true;
    c.item = item;
    c.caret = w.kind == WidgetKind::Notes ? c.text.size() : c.items[size_t(item)].text.size();
    c.caretOn = true;
    if (w.kind == WidgetKind::Reminders) {
        float view = WidgetHeight(w.size) - kReminderTop - kReminderBottom;
        float top = float(item) * kReminderRow;
        c.scroll = std::clamp(c.scroll, std::max(0.f, top + kReminderRow - view), top);
    }
    SetTimer(host_, kCaretTimer, kCaretMs, nullptr);
    if (GetForegroundWindow() != w.surface->Hwnd())
        SetForegroundWindow(w.surface->Hwnd());
    SetFocus(w.surface->Hwnd());
    w.surface->Invalidate();
}

void DesktopWidgets::EndEdit(Widget& w)
{
    WidgetContent& c = w.content;
    if (!c.editing)
        return;
    c.editing = false;
    if (w.kind == WidgetKind::Reminders && c.item >= 0 && c.item < int(c.items.size()) && c.items[size_t(c.item)].text.empty())
        c.items.erase(c.items.begin() + c.item); // nothing was typed
    c.item = -1;
    KillTimer(host_, kSaveTimer);
    Save();
    if (w.surface)
        w.surface->Invalidate();
    PostMessageW(host_, WM_WIDGET_RESTACK, 0, 0); // back under the windows
}

void DesktopWidgets::Insert(Widget& w, const std::wstring& s)
{
    WidgetContent& c = w.content;
    std::wstring& text = w.kind == WidgetKind::Notes ? c.text : c.items[size_t(c.item)].text;
    std::wstring add = s;
    if (w.kind == WidgetKind::Reminders) // one line
        std::replace_if(add.begin(), add.end(), [](wchar_t ch) { return ch == L'\r' || ch == L'\n'; }, L' ');
    else
        add.erase(std::remove(add.begin(), add.end(), L'\r'), add.end());
    c.caret = std::min(c.caret, text.size());
    text.insert(c.caret, add);
    c.caret += add.size();
}

bool DesktopWidgets::Key(Widget& w, UINT m, WPARAM wp)
{
    WidgetContent& c = w.content;
    if (w.kind == WidgetKind::Reminders && (c.item < 0 || c.item >= int(c.items.size())))
        return false;
    std::wstring& text = w.kind == WidgetKind::Notes ? c.text : c.items[size_t(c.item)].text;
    size_t& at = c.caret;
    at = std::min(at, text.size());
    auto back = [&](size_t i) -> size_t { return i >= 2 && IS_LOW_SURROGATE(text[i - 1]) && IS_HIGH_SURROGATE(text[i - 2]) ? i - 2 : i ? i - 1 : 0; };
    auto ahead = [&](size_t i) -> size_t {
        return i + 1 < text.size() && IS_HIGH_SURROGATE(text[i]) && IS_LOW_SURROGATE(text[i + 1]) ? i + 2 : std::min(i + 1, text.size());
    };
    bool changed = false;
    if (m == WM_KEYDOWN) {
        switch (wp) {
        case VK_LEFT:
            at = back(at);
            break;
        case VK_RIGHT:
            at = ahead(at);
            break;
        case VK_HOME: {
            size_t nl = at ? text.rfind(L'\n', at - 1) : std::wstring::npos;
            at = nl == std::wstring::npos ? 0 : nl + 1;
            break;
        }
        case VK_END: {
            size_t nl = text.find(L'\n', at);
            at = nl == std::wstring::npos ? text.size() : nl;
            break;
        }
        case VK_DELETE:
            if (at < text.size()) {
                text.erase(at, ahead(at) - at);
                changed = true;
            }
            break;
        default:
            return false; // WM_CHAR follows for the rest
        }
    } else {
        wchar_t ch = wchar_t(wp);
        if (ch == 27) { // Esc
            EndEdit(w);
            return true;
        }
        if (ch == L'\r') {
            if (w.kind == WidgetKind::Notes) {
                Insert(w, L"\n");
                changed = true;
            } else if (text.empty()) {
                EndEdit(w);
                return true;
            } else { // the next reminder, right below
                int next = c.item + 1;
                c.items.insert(c.items.begin() + next, Reminder{});
                c.editing = false; // the one typed stays as it is
                SaveSoon();
                StartEdit(w, next);
                return true;
            }
        } else if (ch == 8) { // Backspace
            if (at) {
                size_t from = back(at);
                text.erase(from, at - from);
                at = from;
                changed = true;
            }
        } else if (ch == 22) { // Ctrl+V
            if (OpenClipboard(w.surface->Hwnd())) {
                if (HANDLE data = GetClipboardData(CF_UNICODETEXT))
                    if (auto* p = static_cast<const wchar_t*>(GlobalLock(data))) {
                        Insert(w, p);
                        GlobalUnlock(data);
                        changed = true;
                    }
                CloseClipboard();
            }
        } else if (ch >= 0x20 || ch == L'\t') {
            Insert(w, std::wstring(1, ch));
            changed = true;
        } else {
            return true; // other control keys
        }
    }
    if (changed)
        SaveSoon();
    c.caretOn = true;
    SetTimer(host_, kCaretTimer, kCaretMs, nullptr); // the caret stays lit while typing
    w.surface->Invalidate();
    return true;
}

// The input method's composition and candidates next to the caret.
void DesktopWidgets::PlaceImeWindow(Widget& w)
{
    auto caret = std::find_if(w.hits.begin(), w.hits.end(), [](const WidgetHit& h) { return h.id == L"caret"; });
    if (caret == w.hits.end() || !w.surface)
        return;
    float s = w.surface->Scale();
    POINT p = w.surface->ContentToScreen(POINT{LONG(caret->rect.left * s), LONG(caret->rect.top * s)});
    ScreenToClient(w.surface->Hwnd(), &p);
    HIMC imc = ImmGetContext(w.surface->Hwnd());
    if (!imc)
        return;
    COMPOSITIONFORM cf{CFS_POINT, p, {}};
    ImmSetCompositionWindow(imc, &cf);
    CANDIDATEFORM cand{0, CFS_CANDIDATEPOS, POINT{p.x, p.y + LONG((caret->rect.bottom - caret->rect.top) * s) + 4}, {}};
    ImmSetCandidateWindow(imc, &cand);
    ImmReleaseContext(w.surface->Hwnd(), imc);
}

// A world clock's cities: which one to change, then the city in its place (both in menus at `at`).
void DesktopWidgets::PickCity(Widget& w, POINT at)
{
    if (!menu)
        return;
    const std::vector<std::wstring> cities = WidgetCities(w.kind, w.content.cities);
    std::vector<MenuEntry> slots{{Tr(L"Change which city"), 0, false, false, true}};
    for (size_t i = 0; i < cities.size(); ++i)
        slots.push_back({Tr(cities[i].c_str()), int(i) + 1});
    Widget* target = &w;
    menu->Open(std::move(slots), at, MenuPlace::Below, [this, target, at, cities](int slot) {
        if (slot <= 0 || !Has(target))
            return;
        std::vector<MenuEntry> list{{TrFormat(L"Change “%s” to", Tr(cities[size_t(slot - 1)].c_str())), 0, false, false, true}};
        const auto& all = WorldCities();
        for (size_t i = 0; i < all.size(); ++i)
            list.push_back({Tr(all[i].name), int(i) + 1, cities[size_t(slot - 1)] == all[i].name});
        menu->Open(std::move(list), at, MenuPlace::Below, [this, target, slot, cities](int pick) {
            if (pick <= 0 || !Has(target))
                return;
            Widget& w = *target;
            std::vector<std::wstring> chosen = cities;
            chosen[size_t(slot - 1)] = WorldCities()[size_t(pick - 1)].name;
            w.content.cities = std::move(chosen);
            Save();
            w.surface->Invalidate();
            Log(L"widgets: a world clock shows %s", w.content.cities[size_t(slot - 1)].c_str());
        });
    });
}

bool DesktopWidgets::ShellMenuMessage(UINT m, WPARAM w, LPARAM l, LRESULT& result)
{
    return shellMenuMessage_ && (m == WM_INITMENUPOPUP || m == WM_DRAWITEM || m == WM_MEASUREITEM || m == WM_MENUCHAR)
           && shellMenuMessage_(m, w, l, result);
}

void DesktopWidgets::FileMenu(HWND owner, std::wstring zone, WidgetContent::File file)
{
    if (shellMenuMessage_) return;
    HMENU popup = CreatePopupMenu();
    if (!popup) return;
    constexpr UINT firstVerb = 1, lastVerb = 0x6fff, open = 0x7000, rename = 0x7001, collect = 0x7002, restore = 0x7003;
    winrt::com_ptr<IShellItem> item;
    winrt::com_ptr<IContextMenu> context;
    if (SUCCEEDED(SHCreateItemFromParsingName(file.path.c_str(), nullptr, IID_PPV_ARGS(item.put()))))
        item->BindToHandler(nullptr, BHID_SFUIObject, IID_PPV_ARGS(context.put()));
    UINT flags = CMF_NORMAL | ((GetKeyState(VK_SHIFT) & 0x8000) ? CMF_EXTENDEDVERBS : 0);
    if (!context || FAILED(context->QueryContextMenu(popup, 0, firstVerb, lastVerb, flags)))
        AppendMenuW(popup, MF_STRING, open, L"打开");
    AppendMenuW(popup, MF_SEPARATOR, 0, nullptr);
    AppendMenuW(popup, MF_STRING, rename, L"重命名…");
    AppendMenuW(popup, MF_STRING, 0x7004, L"记住归类规则…");
    if (file.onDesktop) AppendMenuW(popup, MF_STRING, collect, L"收纳到此分区");
    if (file.collected) AppendMenuW(popup, MF_STRING, restore, L"放回原桌面");

    auto menu3 = context ? context.try_as<IContextMenu3>() : nullptr;
    auto menu2 = context ? context.try_as<IContextMenu2>() : nullptr;
    shellMenuMessage_ = [&](UINT m, WPARAM wp, LPARAM lp, LRESULT& result) {
        result = 0;
        if (menu3) return menu3->HandleMenuMsg2(m, wp, lp, &result) == S_OK;
        return menu2 && m != WM_MENUCHAR && menu2->HandleMenuMsg(m, wp, lp) == S_OK;
    };
    POINT pt;
    GetCursorPos(&pt);
    SetForegroundWindow(owner);
    UINT cmd = TrackPopupMenuEx(popup, TPM_RETURNCMD | TPM_RIGHTBUTTON, pt.x, pt.y, owner, nullptr);
    shellMenuMessage_ = {};
    DestroyMenu(popup);
    if (cmd >= firstVerb && cmd <= lastVerb && context) {
        CMINVOKECOMMANDINFOEX invoke{sizeof(invoke)};
        invoke.fMask = CMIC_MASK_UNICODE | CMIC_MASK_PTINVOKE;
        invoke.hwnd = owner;
        invoke.lpVerb = MAKEINTRESOURCEA(cmd - firstVerb);
        invoke.lpVerbW = MAKEINTRESOURCEW(cmd - firstVerb);
        invoke.nShow = SW_SHOWNORMAL;
        invoke.ptInvoke = pt;
        context->InvokeCommand(reinterpret_cast<LPCMINVOKECOMMANDINFO>(&invoke));
    } else if (cmd == open) FileAction(L"open", zone, file.path);
    else if (cmd == rename) FileAction(L"rename", zone, file.path);
    else if (cmd == collect) FileAction(L"collect", zone, file.path);
    else if (cmd == restore) FileAction(L"restore", zone, file.path);
    else if (cmd == 0x7004) FileAction(L"learn", zone, file.path);
}

void DesktopWidgets::Menu(Widget& w, const std::wstring& hit)
{
    if (w.kind == WidgetKind::FileZone && !hit.empty() && hit != L"learn") {
        auto file = std::find_if(w.content.files.begin(), w.content.files.end(), [&](const auto& f) { return f.path == hit; });
        if (file != w.content.files.end()) FileMenu(w.surface->Hwnd(), w.content.text, *file);
        return;
    }
    if (!menu)
        return;
    enum { kSmall = 1, kMedium, kLarge, kRemove, kFolder, kNextPhoto, kClearDone, kAdd, kCities, kFileGrid, kFileCards };
    std::vector<MenuEntry> entries;
    auto size = [&](int id, WidgetSize s, const wchar_t* label) { entries.push_back({label, id, w.size == s, !WidgetSupports(w.kind, s)}); };
    size(kSmall, WidgetSize::Small, Tr(L"Small"));
    size(kMedium, WidgetSize::Medium, Tr(L"Medium"));
    size(kLarge, WidgetSize::Large, Tr(L"Large"));
    entries.push_back({});
    if (w.kind == WidgetKind::Photos) {
        entries.push_back({Tr(L"Choose a photo folder…"), kFolder});
        entries.push_back({Tr(L"Next photo"), kNextPhoto});
        entries.push_back({});
    }
    if (HasWorldClocks(w.kind, w.size)) {
        entries.push_back({Tr(L"Choose city…"), kCities});
        entries.push_back({});
    }
    if (w.kind == WidgetKind::Reminders) {
        bool anyDone = std::any_of(w.content.items.begin(), w.content.items.end(), [](const Reminder& r) { return r.done; });
        entries.push_back({Tr(L"Clear completed"), kClearDone, false, !anyDone});
        entries.push_back({});
    }
    if (w.kind == WidgetKind::CodexQuota) entries.push_back({L"刷新 Codex 额度", 104});
    if (w.kind == WidgetKind::Weather && fileOwner_) entries.push_back({L"天气城市…", 105});
    if (fileOwner_ && std::none_of(widgets_.begin(), widgets_.end(), [](const auto& item) { return item->kind == WidgetKind::CodexQuota; }))
        entries.push_back({L"添加 Codex 额度", 103});
    if (w.kind == WidgetKind::FileZone) {
        bool grid = LayoutFileZone(w.content, w.size).grid;
        entries.push_back({L"图标网格", kFileGrid, grid});
        entries.push_back({L"文件卡片", kFileCards, !grid});
        entries.push_back({});
        entries.push_back({L"新建文件分区…", 101});
        entries.push_back({L"管理分类规则…", 102});
        entries.push_back({L"移入开始菜单", 107});
        entries.push_back({L"解散分区…", 106});
    } else entries.push_back({Tr(L"Remove widget"), kRemove});
    entries.push_back({Tr(L"Add Widgets…"), kAdd});
    POINT p;
    GetCursorPos(&p);
    Widget* target = &w;
    menu->Open(std::move(entries), p, MenuPlace::Below, [this, target, p](int cmd) {
        if (cmd == kAdd) {
            if (onAddWidgets)
                onAddWidgets();
            return;
        }
        if (!Has(target))
            return; // gone while the menu was open
        Widget& w = *target;
        switch (cmd) {
        case 101: FileAction(L"new"); break;
        case 102: FileAction(L"manage"); break;
        case 106: FileAction(L"dissolve", w.content.text); break;
        case 107: FileAction(L"menuZone", w.content.text); break;
        case 105: FileAction(L"weatherCity"); break;
        case kFileGrid:
        case kFileCards: {
            const auto before = LayoutFileZone(w.content, w.size);
            const size_t first = size_t(std::min(w.content.scroll, before.maxScroll) / before.rowPitch) * before.columns;
            w.content.fileLayout = cmd == kFileGrid ? L"grid" : L"cards";
            const auto after = LayoutFileZone(w.content, w.size);
            w.content.scroll = std::min(after.RowOffset(first), after.maxScroll);
            w.content.hoveredFile.clear();
            w.surface->Invalidate();
            Save();
            break;
        }
        case 103:
            if (!AddQuota()) MessageBoxW(host_, L"请先缩小或移除一个组件，留出一格空间。", L"Codex 额度", MB_OK);
            break;
        case 104: FileAction(L"quotaRefresh"); break;
        case kSmall:
        case kMedium:
        case kLarge:
            Resize(w, cmd == kSmall ? WidgetSize::Small : cmd == kMedium ? WidgetSize::Medium : WidgetSize::Large);
            break;
        case kFolder:
            PickFolder(w);
            break;
        case kCities:
            PickCity(w, p);
            break;
        case kNextPhoto:
            LoadPhoto(w, true);
            break;
        case kClearDone:
            std::erase_if(w.content.items, [](const Reminder& r) { return r.done; });
            Save();
            w.surface->Invalidate();
            break;
        case kRemove:
            Remove(w); // w is gone
            break;
        }
    });
}

// ---- painting ------------------------------------------------------------------

// Without glass there is no glass shadow: a soft one of its own in the margin, a little lower
// than the face, as under macOS's widgets. Only around the face (it is opaque, and already drawn).
void DesktopWidgets::PaintShadow(Canvas& c, D2D1_RECT_F face)
{
    ID2D1DeviceContext* d2d = c.D2D();
    winrt::com_ptr<ID2D1Factory> factory;
    d2d->GetFactory(factory.put());
    winrt::com_ptr<ID2D1RectangleGeometry> outer;
    winrt::com_ptr<ID2D1RoundedRectangleGeometry> inner;
    winrt::com_ptr<ID2D1PathGeometry> ring;
    winrt::com_ptr<ID2D1GeometrySink> sink;
    const D2D1_RECT_F px = c.Px(face);
    const float r = kWidgetRadius * c.scale, room = 14 * c.scale;
    if (FAILED(factory->CreateRectangleGeometry(D2D1::RectF(px.left - room, px.top - room, px.right + room, px.bottom + room), outer.put())) ||
        FAILED(factory->CreateRoundedRectangleGeometry(D2D1::RoundedRect(px, r, r), inner.put())) || FAILED(factory->CreatePathGeometry(ring.put())) ||
        FAILED(ring->Open(sink.put())) || FAILED(outer->CombineWithGeometry(inner.get(), D2D1_COMBINE_MODE_EXCLUDE, nullptr, sink.get())) ||
        FAILED(sink->Close()))
        return;
    d2d->PushLayer(D2D1::LayerParameters1(D2D1::InfiniteRect(), ring.get()), nullptr);
    for (int i = 9; i >= 1; --i) {
        const float d = float(i);
        c.Fill(D2D1::RectF(face.left - d, face.top - d + 2, face.right + d, face.bottom + d + 2), kWidgetRadius + d, D2D1::ColorF(0, 0, 0, 0.018f));
    }
    d2d->PopLayer();
}

void DesktopWidgets::Paint(Widget& w, Canvas& c)
{
    RECT rect = w.surface->Rect();
    const float width = float(rect.right - rect.left) / c.scale, height = float(rect.bottom - rect.top) / c.scale;
    const auto bounds = D2D1::RectF(0, 0, width, height);
    // Preserve each size's layout. Only its viewport grows or shrinks, so a
    // large face is revealed without squeezing its columns into a small one.
    c.Clip(bounds);
    if (c.panel && material_ != WidgetMaterial::Color)
        c.Fill(bounds, 0, c.theme.panel);
    GetLocalTime(&world_.now);
    if (w.kind == WidgetKind::Reminders)
        w.content.scroll = std::clamp(w.content.scroll, 0.f, ReminderScrollMax(w.content, w.size));
    PaintWidget(c, w.kind, w.size, material_, w.content, world_, w.hits);
    if ((w.hover || w.resizing) && !w.dragging && Resizable(w.kind)) {
        // Both corners use the same quiet arc; either can be dragged to resize.
        const float r = kWidgetRadius, h = height;
        c.Arc(r, h - r, r - 6, 100.f, 170.f, c.theme.subtext, 3.5f);
        c.Arc(width - r, h - r, r - 6, 10.f, 80.f, c.theme.subtext, 3.5f);
    }
    c.Unclip();
    c.MaskRounded(bounds, kWidgetRadius);
    if (material_ == WidgetMaterial::Color)
        PaintShadow(c, bounds);
    if (w.content.editing)
        PlaceImeWindow(w);
}

} // namespace atrium
