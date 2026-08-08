using System;
using System.Windows;

namespace MyAiGen;

public static class Program
{
    [STAThread]
    public static void Main()
    {
        App app = new();
        app.Run(new MainWindow());
    }
}
