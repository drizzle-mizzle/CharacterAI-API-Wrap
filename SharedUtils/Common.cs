﻿using System.Drawing;
using System.Dynamic;

namespace SharedUtils
{
    public static class Common
    {
        public const string WARN_SIGN = "⚠";
        public static readonly string CD = Directory.GetCurrentDirectory();
        public static readonly char SC = Path.DirectorySeparatorChar;


        public static void LogRed(string? title = null, Exception? e = null)
        {
            if (title is not null)
                Log($"{title}\n", ConsoleColor.Red);

            if (e is not null)
                Log($"Exception details:\n{e}\n", ConsoleColor.Red);
        }

        public static void LogGreen(string logText)
            => Log(logText + "\n", ConsoleColor.Green);

        public static void Log(string text, ConsoleColor color = ConsoleColor.Gray)
        {
            Console.ForegroundColor = color;
            Console.Write(text);
            Console.ResetColor();
        }

        public static void WriteToLogFile(string text)
        {
            try
            {
                string path = $"{CD}{SC}log.txt";
                File.AppendAllText(path, text + "\n-------------------------------------\n\n");
            }
            catch (Exception e)
            {
                LogRed("FILE LOG ERROR", e);
            }
        }

        //public static void ClearTemps()
        //{
        //    try { Directory.Delete($"{CD}{SC}puppeteer-temps", true); } catch { }
        //}
    }
}
