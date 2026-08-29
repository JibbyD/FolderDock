using System;
using System.Runtime.InteropServices;

namespace FolderDock
{
    // Enables the same real, GPU-composited blur-behind effect Windows itself
    // uses for the Start menu and other flyouts, via the undocumented
    // SetWindowCompositionAttribute API (works on Windows 10 and 11).
    //
    // NOTE: the current UI does NOT call EnableBlur - the popup is deliberately
    // clear so the live wallpaper shows through. This is kept for reference / in
    // case you want the frosted look back (call it from MainWindow's
    // SourceInitialized handler with the window handle).
    public static class AcrylicHelper
    {
        private enum AccentState
        {
            ACCENT_DISABLED = 0,
            ACCENT_ENABLE_GRADIENT = 1,
            ACCENT_ENABLE_TRANSPARENTGRADIENT = 2,
            ACCENT_ENABLE_BLURBEHIND = 3,
            ACCENT_ENABLE_ACRYLICBLURBEHIND = 4,
            ACCENT_ENABLE_HOSTBACKDROP = 5
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct AccentPolicy
        {
            public AccentState AccentState;
            public int AccentFlags;
            public int GradientColor;
            public int AnimationId;
        }

        private enum WindowCompositionAttribute
        {
            WCA_ACCENT_POLICY = 19
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct WindowCompositionAttributeData
        {
            public WindowCompositionAttribute Attribute;
            public IntPtr Data;
            public int SizeOfData;
        }

        [DllImport("user32.dll")]
        private static extern int SetWindowCompositionAttribute(IntPtr hwnd, ref WindowCompositionAttributeData data);

        public static void EnableBlur(IntPtr hwnd)
        {
            // GradientColor is packed as 0xAABBGGRR for this particular API.
            // Alpha is kept very low (0x01) so the blur itself shows through
            // cleanly - the actual dim/card tinting is drawn by WPF on top.
            var accent = new AccentPolicy
            {
                AccentState = AccentState.ACCENT_ENABLE_ACRYLICBLURBEHIND,
                GradientColor = unchecked((int)0x01FFFFFF)
            };

            int accentSize = Marshal.SizeOf(accent);
            IntPtr accentPtr = Marshal.AllocHGlobal(accentSize);

            try
            {
                Marshal.StructureToPtr(accent, accentPtr, false);

                var data = new WindowCompositionAttributeData
                {
                    Attribute = WindowCompositionAttribute.WCA_ACCENT_POLICY,
                    SizeOfData = accentSize,
                    Data = accentPtr
                };

                SetWindowCompositionAttribute(hwnd, ref data);
            }
            catch
            {
                // If this fails on a given Windows build, the window still
                // works fine - it just won't have the blur effect.
            }
            finally
            {
                Marshal.FreeHGlobal(accentPtr);
            }
        }
    }
}
