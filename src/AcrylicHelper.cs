// ─────────────────────────────────────────────────────────────────────────────
//  AcrylicHelper.cs
//
//  Turns on the frosted "acrylic" blur-behind that Windows itself uses for the
//  Start menu, via the undocumented SetWindowCompositionAttribute API.
//
//  NOT USED by the current build. The popup is deliberately clear so the live
//  wallpaper shows through. This file is kept so the frosted look is one call
//  away: from MainWindow, hook SourceInitialized and call
//  AcrylicHelper.EnableBlur(new WindowInteropHelper(this).Handle).
//
//  Calling an undocumented API means marshalling a struct across to native code
//  by hand — that's what all the [StructLayout] / Marshal.* below is doing.
// ─────────────────────────────────────────────────────────────────────────────

using System;
using System.Runtime.InteropServices;   // DllImport, StructLayout, Marshal

namespace FolderDock
{
    /// <summary>Optional Windows blur-behind. Static; call <see cref="EnableBlur"/> with a window handle.</summary>
    public static class AcrylicHelper
    {
        /// <summary>The kinds of background effect the API supports. We use ACRYLICBLURBEHIND.</summary>
        private enum AccentState
        {
            ACCENT_DISABLED = 0,
            ACCENT_ENABLE_GRADIENT = 1,
            ACCENT_ENABLE_TRANSPARENTGRADIENT = 2,
            ACCENT_ENABLE_BLURBEHIND = 3,             // plain blur (older, cheaper)
            ACCENT_ENABLE_ACRYLICBLURBEHIND = 4,      // acrylic blur (the frosted look)
            ACCENT_ENABLE_HOSTBACKDROP = 5
        }

        // [StructLayout(Sequential)] = lay these fields out in memory in this exact
        // order, so the native side reads them correctly.
        [StructLayout(LayoutKind.Sequential)]
        private struct AccentPolicy
        {
            public AccentState AccentState;   // which effect (from the enum above)
            public int AccentFlags;           // unused here
            public int GradientColor;         // tint colour+alpha, packed as 0xAABBGGRR
            public int AnimationId;           // unused here
        }

        /// <summary>Which window attribute we're setting. 19 = the accent (background) policy.</summary>
        private enum WindowCompositionAttribute
        {
            WCA_ACCENT_POLICY = 19
        }

        // The wrapper struct the API actually takes: "here's an attribute id, a
        // pointer to its data, and how big that data is".
        [StructLayout(LayoutKind.Sequential)]
        private struct WindowCompositionAttributeData
        {
            public WindowCompositionAttribute Attribute;
            public IntPtr Data;       // pointer to an AccentPolicy in unmanaged memory
            public int SizeOfData;    // sizeof(AccentPolicy)
        }

        /// <summary>The undocumented user32 function that applies the effect to a window.</summary>
        [DllImport("user32.dll")]
        private static extern int SetWindowCompositionAttribute(IntPtr hwnd, ref WindowCompositionAttributeData data);

        /// <summary>Applies acrylic blur-behind to the window with handle <paramref name="hwnd"/>.</summary>
        public static void EnableBlur(IntPtr hwnd)
        {
            // Very low alpha (0x01) so the blur shows through cleanly; any visible
            // tint is drawn by WPF (the Card's Background) on top of it.
            var accent = new AccentPolicy
            {
                AccentState = AccentState.ACCENT_ENABLE_ACRYLICBLURBEHIND,
                GradientColor = unchecked((int)0x01FFFFFF)   // 0xAABBGGRR: AA=01, then white
            };

            int accentSize = Marshal.SizeOf(accent);              // bytes the struct occupies
            IntPtr accentPtr = Marshal.AllocHGlobal(accentSize);  // allocate that many bytes of unmanaged memory

            try
            {
                Marshal.StructureToPtr(accent, accentPtr, false); // copy the struct into that memory

                var data = new WindowCompositionAttributeData
                {
                    Attribute = WindowCompositionAttribute.WCA_ACCENT_POLICY,
                    SizeOfData = accentSize,
                    Data = accentPtr                              // point the wrapper at it
                };

                SetWindowCompositionAttribute(hwnd, ref data);    // hand it all to Windows
            }
            catch
            {
                // Undocumented API — if a Windows build rejects it, the window is
                // still perfectly usable, just without the blur.
            }
            finally
            {
                Marshal.FreeHGlobal(accentPtr);   // always release the unmanaged memory
            }
        }
    }
}
