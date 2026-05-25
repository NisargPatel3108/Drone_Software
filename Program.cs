using System;
using System.Diagnostics;
using System.Windows.Forms;
using Microsoft.Win32;

namespace MinimalGCS
{
    static class Program
    {
        [STAThread]
        static void Main()
        {
            try
            {
                SetBrowserFeatureControl();
            }
            catch (Exception ex)
            {
                Console.WriteLine("Failed to set browser emulation: " + ex.Message);
            }

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new MainForm());
        }

        private static void SetBrowserFeatureControl()
        {
            string appName = Process.GetCurrentProcess().ProcessName + ".exe";
            using (var key = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Internet Explorer\Main\FeatureControl\FEATURE_BROWSER_EMULATION"))
            {
                if (key != null)
                {
                    key.SetValue(appName, 11001, RegistryValueKind.DWord);
                }
            }
        }
    }
}
