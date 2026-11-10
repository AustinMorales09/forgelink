namespace ForgeLink.Mobile;

public partial class App : Application
{
    public App(ConnectivitySync sync)
    {
        InitializeComponent();
        sync.Start();
    }

    protected override Window CreateWindow(IActivationState? activationState) =>
        new(new MainPage()) { Title = "ForgeLink Mobile" };
}
