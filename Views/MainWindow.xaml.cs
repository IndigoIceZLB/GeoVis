// Views/MainWindow.xaml.cs
using System;
using System.ComponentModel;
using System.Linq;
using System.Windows;
using GeoVis.ViewModels;
using ScottPlot;
using System.IO;

namespace GeoVis.Views
{
    public partial class MainWindow : Window
    {
        public MainWindow()
        {
            InitializeComponent();

            // 绑定 ViewModel
            var vm = new MainViewModel();
            this.DataContext = vm;

            // 监听 ViewModel 的数据变化，一旦有数据就开始画图
            vm.PropertyChanged += ViewModel_PropertyChanged;

            // 【新增这一行】：监听 ViewModel 发出的 JSON 字符串，并通过 WebView2 投递给 JS
            vm.OnGeoJsonReadyToSend += jsonStr => MapWebView.CoreWebView2.PostWebMessageAsJson(jsonStr);

            // 初始化一个空的坐标系样式
            InitializeChartStyle();

            // 初始化内嵌浏览器引擎
            InitializeWebViewAsync();
        }

        private void InitializeChartStyle()
        {
            MainChart.Plot.Clear();
            MainChart.Plot.Title("网络结构指标逐时演化");

            // 隐藏右侧和顶部的边框，符合学术极简风
            MainChart.Plot.Axes.Right.IsVisible = true;

            MainChart.Refresh();
        }

        private async void InitializeWebViewAsync()
        {
            // 等待 WebView2 核心组件初始化完毕
            await MapWebView.EnsureCoreWebView2Async(null);

            // 获取本地 HTML 文件的绝对路径
            string baseDir = System.AppDomain.CurrentDomain.BaseDirectory;
            string htmlPath = Path.Combine(baseDir, "Assets", "web", "map_template.html");

            if (File.Exists(htmlPath))
            {
                // 导航加载本地 HTML 文件
                MapWebView.CoreWebView2.Navigate(htmlPath);
            }
            else
            {
                MessageBox.Show("找不到地图模板文件，请检查 Assets/web/map_template.html 的生成操作是否设为了‘如果较新则复制’。");
            }
        }

        private void ViewModel_PropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            // 当接收到后端传来的数据时
            if (e.PropertyName == nameof(MainViewModel.NetworkMetricsData))
            {
                var vm = (MainViewModel)sender;
                if (vm.NetworkMetricsData != null && vm.NetworkMetricsData.Any())
                {
                    RenderAcademicChart(vm.NetworkMetricsData);
                }
            }
        }

        private void RenderAcademicChart(System.Collections.Generic.List<Models.HourlyNetworkMetric> data)
        {
            MainChart.Plot.Clear();

            // 1. 数据准备：将你 Python 的日期转成 ScottPlot 识别的数值
            double[] xs = new double[data.Count];
            double[] y_weight = new double[data.Count];
            double[] y_edge = new double[data.Count];

            for (int i = 0; i < data.Count; i++)
            {
                var d = data[i];
                // 拼接 "20230501" 和 "0" 变成具体的时间对象
                DateTime dt = DateTime.ParseExact($"{d.StartDate}{d.StartHour:D2}", "yyyyMMddHH", System.Globalization.CultureInfo.InvariantCulture);
                xs[i] = dt.ToOADate(); // C# 处理时间序列的常规做法
                y_weight[i] = d.TotalWeight;
                y_edge[i] = d.EdgeNumber;
            }

            // 2. 绘制左轴 (总流量) - 复刻 Python 配色 #C06C84
            var sigWeight = MainChart.Plot.Add.Scatter(xs, y_weight);
            sigWeight.Color = Color.FromHex("#C06C84");
            sigWeight.LineWidth = 2.5f;
            sigWeight.MarkerSize = 4;
            sigWeight.Label = "Total Edge Weight";

            // 左侧 Y 轴样式
            MainChart.Plot.Axes.Left.Label.Text = "Total Edge Weight";
            MainChart.Plot.Axes.Left.Label.ForeColor = sigWeight.Color;
            MainChart.Plot.Axes.Left.TickLabelStyle.ForeColor = sigWeight.Color;

            // 3. 绘制右轴 (边数量) - 复刻 Python 配色 #5890AD
            var sigEdge = MainChart.Plot.Add.Scatter(xs, y_edge);
            sigEdge.Color = Color.FromHex("#5890AD");
            sigEdge.LineWidth = 2.5f;
            sigEdge.MarkerSize = 4;
            sigEdge.Label = "Edge Number";

            // 绑定到右侧 Y 轴
            sigEdge.Axes.YAxis = MainChart.Plot.Axes.Right;
            MainChart.Plot.Axes.Right.Label.Text = "Edge Number";
            MainChart.Plot.Axes.Right.Label.ForeColor = sigEdge.Color;
            MainChart.Plot.Axes.Right.TickLabelStyle.ForeColor = sigEdge.Color;

            // 4. X轴样式设置：将其转化为日期时间显示
            MainChart.Plot.Axes.DateTimeTicksBottom();
            MainChart.Plot.Axes.Bottom.Label.Text = "Date and Hour";

            // 5. 图例设置
            var legend = MainChart.Plot.ShowLegend();
            legend.Alignment = Alignment.UpperCenter;
            legend.Orientation = Orientation.Horizontal;

            // 6. 刷新画布
            MainChart.Plot.Title("Hourly Evolution of Network Structure Metrics");
            // 限制初始视角只显示前 3 天（3天 * 24小时 = 72），这样 X 轴就会拉得很开
            MainChart.Plot.Axes.SetLimitsX(xs[0], xs[Math.Min(72, xs.Length - 1)]);
            MainChart.Refresh();
        }
    }
}