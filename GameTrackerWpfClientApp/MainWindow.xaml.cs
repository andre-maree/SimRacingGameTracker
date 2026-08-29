using System;
using System.Windows;

namespace GameTrackerWpfClientApp
{
    /// <summary>
    /// Shell window whose entire client area is a <c>BlazorWebView</c>.
    /// </summary>
    public partial class MainWindow : Window
    {
        public MainWindow(IServiceProvider services)
        {
            InitializeComponent();

            // Assigned in code-behind rather than bound in XAML: the WebView resolves its
            // root component through this provider during InitializeComponent's layout
            // pass, and a binding would not have been evaluated yet.
            BlazorWebView.Services = services;
        }
    }
}