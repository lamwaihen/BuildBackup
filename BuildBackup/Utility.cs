using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Windows.UI.Core;

namespace BuildBackup
{
    class Utility
    {
        static public CoreDispatcher UIdispatcher = Windows.ApplicationModel.Core.CoreApplication.MainView.CoreWindow.Dispatcher;
        static public void UIThreadExecute(Action action)
        {
            if (UIdispatcher.HasThreadAccess)
                action();
            else
                InnerExecute(action).Wait();
        }
        static private async Task InnerExecute(Action action)
        {
            await UIdispatcher.RunAsync(CoreDispatcherPriority.Normal, () => action());
        }
    }
}
