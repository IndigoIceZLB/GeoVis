// ViewModels/MainViewModel.cs
using CommunityToolkit.Mvvm.ComponentModel;

namespace GeoVis.ViewModels
{
    // 必须声明为 partial 类，Toolkit 的源生成器会在后台自动生成属性通知代码
    public partial class MainViewModel : ObservableObject
    {
        // 使用 [ObservableProperty] 标签，自动生成公有的 ApplicationTitle 属性及 OnPropertyChanged 逻辑
        [ObservableProperty]
        private string _applicationTitle = "GeoVis - 科研空间数据分析工具";

        [ObservableProperty]
        private string _statusMessage = "就绪 - 100% 离线模式";

        public MainViewModel()
        {
            // 此处后续可用于初始化数据库连接、加载本地配置等
        }
    }
}