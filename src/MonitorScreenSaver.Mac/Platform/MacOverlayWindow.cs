using System.IO;
using MonitorScreenSaver.Core;
using MonitorScreenSaver.Mac.Interop;

namespace MonitorScreenSaver.Mac;

/// <summary>
/// A borderless, non-activating NSPanel at screensaver level, sized to one display.
/// Three appearances, resolved from a <see cref="MonitorConfig"/>:
///
///   TrueBlack — opaque black window (alpha 1.0).
///   Dim       — the same window at partial alpha. macOS is always composited, so
///               unlike WPF's AllowsTransparency there is no separate rendering path
///               and no rebuild crossing the opaque/translucent boundary: black↔dim
///               morphs in place.
///   Video     — an AVPlayerLayer on the content view, driven by AVQueuePlayer +
///               AVPlayerLooper (gapless loop, hardware decode). Muted, and
///               preventsDisplaySleepDuringVideoPlayback is switched off so our own
///               playback can never hold the displays we just covered.
///
/// Only a mode change to/from Video, or a different video file, forces a rebuild.
/// All calls must happen on the main thread (the engine's run loop).
///
/// WakeRequested is never raised here (unlike the Windows overlay): the idle clock
/// already counts every input event, so the engine unblanks within one poll tick.
/// </summary>
public sealed class MacOverlayWindow : IOverlayWindow
{
    private readonly PixelRect _bounds;
    private MonitorConfig _cfg;

    private IntPtr _window;
    private IntPtr _player;
    private IntPtr _looper;
    private IntPtr _playerLayer;
    private bool _visible;

    public event Action? WakeRequested { add { } remove { } }

    public PixelRect BuiltBounds => _bounds;

    public bool IsVisible => _visible;

    public bool VideoPlaying => _visible && _player != IntPtr.Zero;

    public MacOverlayWindow(DisplayTarget target, MonitorConfig cfg)
    {
        _bounds = target.Bounds;
        // Snapshot: the caller hands us the live settings object, and TryApply diffs
        // against what this window was actually built with.
        _cfg = cfg with { };

        AppKit.EnsureApplication();

        var pool = ObjC.objc_autoreleasePoolPush();
        try
        {
            var panel = ObjC.Send(ObjC.Class("NSPanel"), ObjC.Sel("alloc"));
            _window = ObjC.SendInitWindow(panel, ObjC.Sel("initWithContentRect:styleMask:backing:defer:"),
                CocoaFrame(), AppKit.StyleMaskBorderless | AppKit.StyleMaskNonactivatingPanel,
                AppKit.BackingStoreBuffered, false);

            // We own the lifetime; orderOut/close must not deallocate behind our back.
            ObjC.SendVoid(_window, ObjC.Sel("setReleasedWhenClosed:"), false);

            // Above everything, on every Space, unmoved by Mission Control, and allowed
            // to sit over fullscreen Spaces.
            ObjC.SendVoid(_window, ObjC.Sel("setLevel:"), AppKit.ScreenSaverWindowLevel);
            ObjC.SendVoid(_window, ObjC.Sel("setCollectionBehavior:"),
                (nint)(AppKit.BehaviorCanJoinAllSpaces | AppKit.BehaviorStationary | AppKit.BehaviorFullScreenAuxiliary));

            ObjC.SendVoid(_window, ObjC.Sel("setBackgroundColor:"),
                ObjC.Send(ObjC.Class("NSColor"), ObjC.Sel("blackColor")));
            ObjC.SendVoid(_window, ObjC.Sel("setHasShadow:"), false);
            ObjC.SendVoid(_window, ObjC.Sel("setHidesOnDeactivate:"), false);
            // Eat clicks rather than passing them to whatever is underneath.
            ObjC.SendVoid(_window, ObjC.Sel("setIgnoresMouseEvents:"), false);

            ApplyAlpha(_cfg);

            if (_cfg.Mode == OverlayMode.Video) BuildVideo();
        }
        finally
        {
            ObjC.objc_autoreleasePoolPop(pool);
        }
    }

    // ------------------------------------------------------------------ video

    private void BuildVideo()
    {
        var path = _cfg.VideoPath;

        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            // Degrade to true black rather than failing: on OLED that is the best
            // possible fallback anyway. Recorded so it doesn't look like "video ignored".
            CrashLog.Write("MacOverlayWindow.Video",
                new FileNotFoundException("Screensaver video not found; showing black.", path));
            return;
        }

        var pool = ObjC.objc_autoreleasePoolPush();
        try
        {
            var nsPath = CF.CreateString(path);
            try
            {
                var url = ObjC.Send(ObjC.Class("NSURL"), ObjC.Sel("fileURLWithPath:"), nsPath);
                var item = ObjC.Send(ObjC.Class("AVPlayerItem"), ObjC.Sel("playerItemWithURL:"), url);

                _player = ObjC.Send(ObjC.Send(ObjC.Class("AVQueuePlayer"), ObjC.Sel("alloc")), ObjC.Sel("init"));

                // The looper owns the queue: template item re-enqueued for a gapless loop.
                _looper = ObjC.Send(ObjC.Class("AVPlayerLooper"),
                    ObjC.Sel("playerLooperWithPlayer:templateItem:"), _player, item);
                ObjC.SendVoid(_looper, ObjC.Sel("retain"));

                // Muted always — a screensaver must not make noise. And AVPlayer files
                // its own display-sleep assertion by default, which is exactly the
                // self-request problem the engine guards against; turn it off at the source.
                ObjC.SendVoid(_player, ObjC.Sel("setMuted:"), true);
                ObjC.SendVoid(_player, ObjC.Sel("setPreventsDisplaySleepDuringVideoPlayback:"), false);

                _playerLayer = ObjC.Send(ObjC.Class("AVPlayerLayer"), ObjC.Sel("playerLayerWithPlayer:"), _player);
                ObjC.SendVoid(_playerLayer, ObjC.Sel("retain"));
                ObjC.SendVoid(_playerLayer, ObjC.Sel("setVideoGravity:"), Gravity(_cfg.VideoStretch));
                ObjC.SendVoid(_playerLayer, ObjC.Sel("setFrame:"),
                    new CG.CGRect { X = 0, Y = 0, Width = _bounds.Width, Height = _bounds.Height });

                var contentView = ObjC.Send(_window, ObjC.Sel("contentView"));
                ObjC.SendVoid(contentView, ObjC.Sel("setWantsLayer:"), true);
                ObjC.SendVoid(ObjC.Send(contentView, ObjC.Sel("layer")), ObjC.Sel("addSublayer:"), _playerLayer);
            }
            finally
            {
                CF.CFRelease(nsPath);
            }
        }
        catch (Exception ex)
        {
            // Bad file or AVFoundation refusing the container: log, drop the pipeline,
            // stay black — same fallback as the Windows MediaFailed path.
            CrashLog.Write($"MacOverlayWindow.Video ({path})", ex);
            TearDownVideo();
        }
        finally
        {
            ObjC.objc_autoreleasePoolPop(pool);
        }
    }

    private void TearDownVideo()
    {
        if (_player != IntPtr.Zero) ObjC.SendVoid(_player, ObjC.Sel("pause"));

        if (_playerLayer != IntPtr.Zero)
        {
            ObjC.SendVoid(_playerLayer, ObjC.Sel("removeFromSuperlayer"));
            ObjC.SendVoid(_playerLayer, ObjC.Sel("release"));
            _playerLayer = IntPtr.Zero;
        }

        if (_looper != IntPtr.Zero)
        {
            ObjC.SendVoid(_looper, ObjC.Sel("release"));
            _looper = IntPtr.Zero;
        }

        if (_player != IntPtr.Zero)
        {
            ObjC.SendVoid(_player, ObjC.Sel("release"));
            _player = IntPtr.Zero;
        }
    }

    private static IntPtr Gravity(VideoStretch stretch) => stretch switch
    {
        VideoStretch.Fill => AVF.GravityResizeAspectFill,
        VideoStretch.Stretch => AVF.GravityResize,
        _ => AVF.GravityResizeAspect,
    };

    // ------------------------------------------------------------------ appearance

    private void ApplyAlpha(MonitorConfig cfg)
    {
        var alpha = cfg.Mode == OverlayMode.Dim ? cfg.Alpha / 255.0 : 1.0;
        ObjC.SendVoid(_window, ObjC.Sel("setAlphaValue:"), alpha);
        ObjC.SendVoid(_window, ObjC.Sel("setOpaque:"), alpha >= 1.0);
    }

    /// <summary>
    /// Applies a config change in place when possible. Rebuild is only demanded when
    /// the video pipeline itself must change: a mode change to/from Video, or a
    /// different file. Black↔dim is a pure alpha change here.
    /// </summary>
    public bool TryApply(MonitorConfig cfg)
    {
        var wasVideo = _cfg.Mode == OverlayMode.Video;
        var isVideo = cfg.Mode == OverlayMode.Video;

        if (wasVideo != isVideo) return false;

        if (isVideo)
        {
            if (!string.Equals(cfg.VideoPath, _cfg.VideoPath, StringComparison.OrdinalIgnoreCase))
                return false;

            if (cfg.VideoStretch != _cfg.VideoStretch && _playerLayer != IntPtr.Zero)
                ObjC.SendVoid(_playerLayer, ObjC.Sel("setVideoGravity:"), Gravity(cfg.VideoStretch));
        }
        else
        {
            ApplyAlpha(cfg);
        }

        _cfg = cfg with { };
        return true;
    }

    // ------------------------------------------------------------------ show / hide

    /// <summary>
    /// CGDisplayBounds is top-left-origin global space; Cocoa frames are bottom-left
    /// origin relative to the primary display. Convert via the primary's height.
    /// </summary>
    private CG.CGRect CocoaFrame()
    {
        var primary = CG.CGDisplayBounds(CG.CGMainDisplayID());

        return new CG.CGRect
        {
            X = _bounds.Left,
            Y = primary.Height - _bounds.Bottom,
            Width = Math.Max(_bounds.Width, 1),
            Height = Math.Max(_bounds.Height, 1),
        };
    }

    public void ShowOverlay()
    {
        if (_window == IntPtr.Zero) return;

        var wasVisible = _visible;

        ApplyBounds();
        ObjC.SendVoid(_window, ObjC.Sel("orderFrontRegardless"));
        _visible = true;

        if (_player != IntPtr.Zero) ObjC.SendVoid(_player, ObjC.Sel("play"));

        // Hide the cursor — the Windows overlay's Cursor=None. Hide/show calls are
        // refcounted per connection, so each window's balanced pair composes; only
        // the hidden→visible transition may call it or ShowAll/Reassert would stack
        // unbalanced hides. See CG.EnableCursorInBackground for why this needs the
        // private CGS property (and why failure is treated as cosmetic).
        if (!wasVisible)
        {
            try
            {
                CG.EnableCursorInBackground();
                CG.CGDisplayHideCursor(CG.CGMainDisplayID());
            }
            catch (Exception ex)
            {
                CrashLog.Write("MacOverlayWindow.HideCursor", ex);
            }
        }
    }

    public void HideOverlay()
    {
        if (_window == IntPtr.Zero) return;

        // Pause, not just hide: an invisible window must not keep decoding.
        if (_player != IntPtr.Zero) ObjC.SendVoid(_player, ObjC.Sel("pause"));

        if (_visible)
        {
            try
            {
                CG.CGDisplayShowCursor(CG.CGMainDisplayID());
            }
            catch (Exception ex)
            {
                CrashLog.Write("MacOverlayWindow.ShowCursor", ex);
            }
        }

        ObjC.SendVoid(_window, ObjC.Sel("orderOut:"), IntPtr.Zero);
        _visible = false;
    }

    /// <summary>Re-asserts frame, level and z-order; cheap, called by the watchdog.</summary>
    public void ApplyBounds()
    {
        if (_window == IntPtr.Zero) return;

        ObjC.SendVoid(_window, ObjC.Sel("setFrame:display:"), CocoaFrame(), true);
        ObjC.SendVoid(_window, ObjC.Sel("setLevel:"), AppKit.ScreenSaverWindowLevel);

        if (_visible) ObjC.SendVoid(_window, ObjC.Sel("orderFrontRegardless"));
    }

    public void Close()
    {
        if (_window == IntPtr.Zero) return;

        // Balances the cursor-hide refcount if we are being closed while visible.
        HideOverlay();

        TearDownVideo();

        ObjC.SendVoid(_window, ObjC.Sel("orderOut:"), IntPtr.Zero);
        ObjC.SendVoid(_window, ObjC.Sel("close"));
        ObjC.SendVoid(_window, ObjC.Sel("release"));
        _window = IntPtr.Zero;
        _visible = false;
    }
}

public sealed class MacOverlayFactory : IOverlayFactory
{
    public IOverlayWindow Create(DisplayTarget target, MonitorConfig cfg) => new MacOverlayWindow(target, cfg);
}
