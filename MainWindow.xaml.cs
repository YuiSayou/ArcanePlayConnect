using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;
using Windows.Foundation;
using Windows.Foundation.Collections;
using ArcanePlayConnect.UI.ViewModels;
using WinUIEx;

namespace ArcanePlayConnect
{
    public sealed partial class MainWindow : WindowEx
    {
        private const int GWLP_WNDPROC = -4;

        private delegate IntPtr WndProcDelegate(IntPtr hWnd, uint msg, nuint wParam, nint lParam);
        private WndProcDelegate? _newWndProc;
        private IntPtr _oldWndProc;

        [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
        private static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

        [DllImport("user32.dll", EntryPoint = "CallWindowProcW")]
        private static extern IntPtr CallWindowProc(IntPtr lpPrevWndFunc, IntPtr hWnd, uint msg, nuint wParam, nint lParam);

        public MainWindow()
        {
            InitializeComponent();

            // Window configuration
            Title = "ArcanePlayConnect";
            ExtendsContentIntoTitleBar = true;
            SetTitleBar(null);

            RootFrame.Navigate(typeof(UI.Views.ShellPage));

            // Initialize keyboard shortcut service with window handle
            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
            var vm = MainViewModel.Instance;
            vm.ShortcutService.Initialize(hwnd);

            // Subclass the window to intercept WM_HOTKEY messages
            _newWndProc = new WndProcDelegate(WndProc);
            _oldWndProc = SetWindowLongPtr(hwnd, GWLP_WNDPROC,
                Marshal.GetFunctionPointerForDelegate(_newWndProc));
        }

        private IntPtr WndProc(IntPtr hWnd, uint msg, nuint wParam, nint lParam)
        {
            MainViewModel.Instance.ShortcutService.ProcessMessage(msg, wParam);
            return CallWindowProc(_oldWndProc, hWnd, msg, wParam, lParam);
        }
    }
}
