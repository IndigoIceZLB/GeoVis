// ====== 【新增：Views/DualMapWindow.xaml.cs】 ======
using System.IO;
using System.Windows;
using GeoVis.ViewModels;

namespace GeoVis.Views
{
    public partial class DualMapWindow : Window
    {
        public DualMapWindow()
        {
            InitializeComponent();
            InitializeWebViewAsync();

            var vm = this.DataContext as DualMapViewModel;
            vm.OnGeoJsonReadyToSend += jsonStr =>
            {
                if (DualWebView != null && DualWebView.CoreWebView2 != null)
                {
                    DualWebView.CoreWebView2.PostWebMessageAsJson(jsonStr);
                }
            };
        }

        private async void InitializeWebViewAsync()
        {
            await DualWebView.EnsureCoreWebView2Async(null);
            string baseDir = System.AppDomain.CurrentDomain.BaseDirectory;
            string htmlPath = Path.Combine(baseDir, "Assets", "web", "dual_map_template.html");

            if (File.Exists(htmlPath))
            {
                DualWebView.CoreWebView2.Navigate(htmlPath);
            }
            else
            {
                MessageBox.Show("找不到 dual_map_template.html，请检查 Assets/web 目录！");
            }
        }
    }
}