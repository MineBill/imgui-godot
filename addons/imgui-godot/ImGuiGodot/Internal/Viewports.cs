using Godot;
using ImGuiNET;
using System;
using System.Runtime.InteropServices;
using Vector2 = System.Numerics.Vector2;

namespace ImGuiGodot.Internal;

internal class GodotImGuiWindow : IDisposable
{
    private readonly GCHandle _gcHandle;
    private readonly ImGuiViewportPtr _vp;

    public Window GodotWindow { get; init; }
    public SubViewport LayerSvp { get; init; }

    public GodotImGuiWindow(ImGuiViewportPtr vp)
    {
        _gcHandle = GCHandle.Alloc(this);
        _vp = vp;
        _vp.PlatformHandle = (IntPtr)_gcHandle;

        Rect2i winRect = new(_vp.Pos.ToVector2i(), _vp.Size.ToVector2i());

        ImGuiLayer.Instance.GetViewport().GuiEmbedSubwindows = false;

        GodotWindow = new Window()
        {
            Borderless = true,
            Position = winRect.Position,
            Size = winRect.Size,
            Transparent = true,
            TransparentBg = true
        };

        GodotWindow.CloseRequested += () => _vp.PlatformRequestClose = true;
        GodotWindow.SizeChanged += () => _vp.PlatformRequestResize = true;
        GodotWindow.WindowInput += OnWindowInput;

        ImGuiLayer.Instance.AddChild(GodotWindow);

        // need to do this after AddChild
        GodotWindow.Transparent = true;

        Util.AddLayerSubViewport(GodotWindow, out SubViewportContainer svpContainer, out SubViewport svp);
        LayerSvp = svp;

        State.Renderer.InitViewport(LayerSvp);
        RenderingServer.ViewportSetTransparentBackground(GodotWindow.GetViewportRid(), true);
    }

    public GodotImGuiWindow(ImGuiViewportPtr vp, Window gw)
    {
        _gcHandle = GCHandle.Alloc(this);
        _vp = vp;
        _vp.PlatformHandle = (IntPtr)_gcHandle;
        GodotWindow = gw;
    }

    public void Dispose()
    {
        if (GodotWindow.GetParent() != null)
        {
            GodotWindow.QueueFree();
        }
        _gcHandle.Free();
    }

    private void OnWindowInput(InputEvent evt)
    {
        Input.ProcessInput(evt, GodotWindow);
    }

    public void ShowWindow()
    {
        GodotWindow.Show();
    }

    public void SetWindowPos(Vector2i pos)
    {
        GodotWindow.Position = pos;
    }

    public Vector2i GetWindowPos()
    {
        return GodotWindow.Position;
    }

    public void SetWindowSize(Vector2i size)
    {
        GodotWindow.Size = size;
    }

    public Vector2i GetWindowSize()
    {
        return GodotWindow.Size;
    }

    public void SetWindowFocus()
    {
        GodotWindow.GrabFocus();
    }

    public bool GetWindowFocus()
    {
        return GodotWindow.HasFocus();
    }

    public bool GetWindowMinimized()
    {
        return GodotWindow.Mode.HasFlag(Window.ModeEnum.Minimized);
    }

    public void SetWindowTitle(string title)
    {
        GodotWindow.Title = title;
    }
}

internal static class Viewports
{
    [DllImport("cimgui", CallingConvention = CallingConvention.Cdecl)]
    private static extern unsafe void ImGuiPlatformIO_Set_Platform_GetWindowPos(ImGuiPlatformIO* platform_io, IntPtr funcPtr);
    [DllImport("cimgui", CallingConvention = CallingConvention.Cdecl)]
    private static extern unsafe void ImGuiPlatformIO_Set_Platform_GetWindowSize(ImGuiPlatformIO* platform_io, IntPtr funcPtr);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void Platform_CreateWindow(ImGuiViewportPtr vp);
    private static readonly Platform_CreateWindow _createWindow = Godot_CreateWindow;

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void Platform_DestroyWindow(ImGuiViewportPtr vp);
    private static readonly Platform_DestroyWindow _destroyWindow = Godot_DestroyWindow;

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void Platform_ShowWindow(ImGuiViewportPtr vp);
    private static readonly Platform_ShowWindow _showWindow = Godot_ShowWindow;

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void Platform_SetWindowPos(ImGuiViewportPtr vp, Vector2 pos);
    private static readonly Platform_SetWindowPos _setWindowPos = Godot_SetWindowPos;

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void Platform_GetWindowPos(ImGuiViewportPtr vp, out Vector2 pos);
    private static readonly Platform_GetWindowPos _getWindowPos = Godot_GetWindowPos;

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void Platform_SetWindowSize(ImGuiViewportPtr vp, Vector2 pos);
    private static readonly Platform_SetWindowSize _setWindowSize = Godot_SetWindowSize;

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void Platform_GetWindowSize(ImGuiViewportPtr vp, out Vector2 size);
    private static readonly Platform_GetWindowSize _getWindowSize = Godot_GetWindowSize;

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void Platform_SetWindowFocus(ImGuiViewportPtr vp);
    private static readonly Platform_SetWindowFocus _setWindowFocus = Godot_SetWindowFocus;

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate bool Platform_GetWindowFocus(ImGuiViewportPtr vp);
    private static readonly Platform_GetWindowFocus _getWindowFocus = Godot_GetWindowFocus;

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate bool Platform_GetWindowMinimized(ImGuiViewportPtr vp);
    private static readonly Platform_GetWindowMinimized _getWindowMinimized = Godot_GetWindowMinimized;

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void Platform_SetWindowTitle(ImGuiViewportPtr vp, string title);
    private static readonly Platform_SetWindowTitle _setWindowTitle = Godot_SetWindowTitle;

    //private static bool _wantUpdateMonitors = true;
    private static GodotImGuiWindow _mainWindow;

    internal static Vector2 ToImVec2(this Vector2i v)
    {
        return new Vector2(v.x, v.y);
    }

    internal static Vector2i ToVector2i(this Vector2 v)
    {
        return new Vector2i((int)v.X, (int)v.Y);
    }

    private static void UpdateMonitors()
    {
        var pio = ImGui.GetPlatformIO();
        int screenCount = DisplayServer.GetScreenCount();

        // workaround for lack of ImVector constructor
        unsafe
        {
            int bytes = screenCount * sizeof(ImGuiPlatformMonitor);
            if (pio.NativePtr->Monitors.Data != IntPtr.Zero)
                ImGui.MemFree(pio.NativePtr->Monitors.Data);
            *&pio.NativePtr->Monitors.Data = ImGui.MemAlloc((uint)bytes);
            *&pio.NativePtr->Monitors.Capacity = screenCount;
            *&pio.NativePtr->Monitors.Size = screenCount;
        }

        for (int i = 0; i < screenCount; ++i)
        {
            var monitor = pio.Monitors[i];
            monitor.MainPos = DisplayServer.ScreenGetPosition(i).ToImVec2();
            monitor.MainSize = DisplayServer.ScreenGetSize(i).ToImVec2();
            monitor.DpiScale = DisplayServer.ScreenGetScale(i);

            var r = DisplayServer.ScreenGetUsableRect(i);
            monitor.WorkPos = r.Position.ToImVec2();
            monitor.WorkSize = r.Size.ToImVec2();
        }
    }

    private static unsafe void InitPlatformInterface()
    {
        var pio = ImGui.GetPlatformIO().NativePtr;

        pio->Platform_CreateWindow = Marshal.GetFunctionPointerForDelegate(_createWindow);
        pio->Platform_DestroyWindow = Marshal.GetFunctionPointerForDelegate(_destroyWindow);
        pio->Platform_ShowWindow = Marshal.GetFunctionPointerForDelegate(_showWindow);
        pio->Platform_SetWindowPos = Marshal.GetFunctionPointerForDelegate(_setWindowPos);
        //pio->Platform_GetWindowPos = Marshal.GetFunctionPointerForDelegate(_getWindowPos);
        pio->Platform_SetWindowSize = Marshal.GetFunctionPointerForDelegate(_setWindowSize);
        //pio->Platform_GetWindowSize = Marshal.GetFunctionPointerForDelegate(_getWindowSize);
        pio->Platform_SetWindowFocus = Marshal.GetFunctionPointerForDelegate(_setWindowFocus);
        pio->Platform_GetWindowFocus = Marshal.GetFunctionPointerForDelegate(_getWindowFocus);
        pio->Platform_GetWindowMinimized = Marshal.GetFunctionPointerForDelegate(_getWindowMinimized);
        pio->Platform_SetWindowTitle = Marshal.GetFunctionPointerForDelegate(_setWindowTitle);

        ImGuiPlatformIO_Set_Platform_GetWindowPos(pio, Marshal.GetFunctionPointerForDelegate(_getWindowPos));
        ImGuiPlatformIO_Set_Platform_GetWindowSize(pio, Marshal.GetFunctionPointerForDelegate(_getWindowSize));
    }

    public static void Init()
    {
        _mainWindow = new(ImGui.GetMainViewport(), ImGuiLayer.Instance.GetViewport() as Window);

        InitPlatformInterface();
        UpdateMonitors();
    }

    public static void RenderViewports()
    {
        var pio = ImGui.GetPlatformIO();
        for (int i = 1; i < pio.Viewports.Size; i++)
        {
            var vp = pio.Viewports[i];
            var window = (GodotImGuiWindow)GCHandle.FromIntPtr(vp.PlatformHandle).Target;
            State.Renderer.RenderDrawData(window.LayerSvp, vp.DrawData);
        }
    }

    private static void Godot_CreateWindow(ImGuiViewportPtr vp)
    {
        _ = new GodotImGuiWindow(vp);
    }

    private static void Godot_DestroyWindow(ImGuiViewportPtr vp)
    {
        if (vp.PlatformHandle != IntPtr.Zero)
        {
            var window = (GodotImGuiWindow)GCHandle.FromIntPtr(vp.PlatformHandle).Target;
            window.Dispose();
            vp.PlatformHandle = IntPtr.Zero;
        }
    }

    private static void Godot_ShowWindow(ImGuiViewportPtr vp)
    {
        var window = (GodotImGuiWindow)GCHandle.FromIntPtr(vp.PlatformHandle).Target;
        window.ShowWindow();
    }

    private static void Godot_SetWindowPos(ImGuiViewportPtr vp, Vector2 pos)
    {
        var window = (GodotImGuiWindow)GCHandle.FromIntPtr(vp.PlatformHandle).Target;
        window.SetWindowPos(pos.ToVector2i());
    }

    private static void Godot_GetWindowPos(ImGuiViewportPtr vp, out Vector2 pos)
    {
        var window = (GodotImGuiWindow)GCHandle.FromIntPtr(vp.PlatformHandle).Target;
        pos = window.GetWindowPos().ToImVec2();
    }

    private static void Godot_SetWindowSize(ImGuiViewportPtr vp, Vector2 size)
    {
        var window = (GodotImGuiWindow)GCHandle.FromIntPtr(vp.PlatformHandle).Target;
        window.SetWindowSize(size.ToVector2i());
    }

    private static void Godot_GetWindowSize(ImGuiViewportPtr vp, out Vector2 size)
    {
        var window = (GodotImGuiWindow)GCHandle.FromIntPtr(vp.PlatformHandle).Target;
        size = window.GetWindowSize().ToImVec2();
    }

    private static void Godot_SetWindowFocus(ImGuiViewportPtr vp)
    {
        var window = (GodotImGuiWindow)GCHandle.FromIntPtr(vp.PlatformHandle).Target;
        window.SetWindowFocus();
    }

    private static bool Godot_GetWindowFocus(ImGuiViewportPtr vp)
    {
        var window = (GodotImGuiWindow)GCHandle.FromIntPtr(vp.PlatformHandle).Target;
        return window.GetWindowFocus();
    }

    private static bool Godot_GetWindowMinimized(ImGuiViewportPtr vp)
    {
        var window = (GodotImGuiWindow)GCHandle.FromIntPtr(vp.PlatformHandle).Target;
        return window.GetWindowMinimized();
    }

    private static void Godot_SetWindowTitle(ImGuiViewportPtr vp, string title)
    {
        var window = (GodotImGuiWindow)GCHandle.FromIntPtr(vp.PlatformHandle).Target;
        window.SetWindowTitle(title);
    }
}
