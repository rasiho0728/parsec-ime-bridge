using System;
using System.Threading;
using System.Windows.Forms;

namespace ParsecImeBridge
{
    static class Program
    {
        [STAThread]
        static void Main()
        {
            using (var mutex = new Mutex(true, @"Local\ParsecImeBridge", out var first))
            {
                if (!first)
                {
                    MessageBox.Show("Parsec IME Bridge가 이미 실행 중입니다. 트레이 아이콘을 확인하세요.",
                        "Parsec IME Bridge", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return;
                }

                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                try
                {
                    Application.Run(new TrayApp());
                }
                catch (InvalidOperationException ex)
                {
                    MessageBox.Show(ex.Message, "Parsec IME Bridge", MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
            }
        }
    }
}
