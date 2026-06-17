using NumberPlate.Pages;

namespace NumberPlate
{
    public partial class App : Application
    {
        //private readonly FacePage _facePage;

        public App()
        {
            InitializeComponent(); 
            //_facePage = facePage;
        }

        protected override Window CreateWindow(IActivationState? activationState)
        {
            return new Window(new Text());
        }
    }
}