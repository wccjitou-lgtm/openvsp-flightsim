using System;
using System.Collections.Generic;

namespace FlightDynamics.Tests
{
    /// <summary>
    /// 轻量测试框架 (零依赖, 不依赖 NUnit/xunit —— 本机无 dotnet CLI 与 NuGet)。
    /// 用法: TestSuite.Add("名称", 委托); Program.Main 统一执行并统计。
    /// </summary>
    public static class TestSuite
    {
        public static readonly List<KeyValuePair<string, Action>> Tests =
            new List<KeyValuePair<string, Action>>();

        private static int _currentFailures;

        public static void Add(string name, Action test) { Tests.Add(new KeyValuePair<string, Action>(name, test)); }

        public static int RunAll()
        {
            int passed = 0, failed = 0;
            foreach (var t in Tests)
            {
                _currentFailures = 0;
                try
                {
                    t.Value();
                }
                catch (Exception ex)
                {
                    Fail("未捕获异常: " + ex.Message);
                }
                if (_currentFailures == 0)
                {
                    passed++;
                    Console.WriteLine("[PASS] " + t.Key);
                }
                else
                {
                    failed++;
                    Console.WriteLine("[FAIL] " + t.Key + "  (" + _currentFailures + " 处断言失败)");
                }
            }
            Console.WriteLine("----------------------------------------------------------");
            Console.WriteLine(string.Format("总计 {0} 项: {1} 通过, {2} 失败", Tests.Count, passed, failed));
            return failed;
        }

        // ---- 断言 ----
        public static void AssertTrue(bool cond, string msg)
        {
            if (!cond) Fail(msg);
        }

        public static void AssertClose(double actual, double expected, double tol, string what)
        {
            if (!(System.Math.Abs(actual - expected) <= tol))
                Fail(string.Format("{0}: 实际 {1} vs 期望 {2} (容差 {3})", what, actual, expected, tol));
        }

        public static void AssertLess(double a, double b, string msg)
        {
            if (!(a < b)) Fail(string.Format("{0}: 需 {1} < {2}", msg, a, b));
        }

        public static void AssertGreater(double a, double b, string msg)
        {
            if (!(a > b)) Fail(string.Format("{0}: 需 {1} > {2}", msg, a, b));
        }

        internal static void Fail(string msg)
        {
            _currentFailures++;
            Console.WriteLine("        断言失败: " + msg);
        }
    }
}
