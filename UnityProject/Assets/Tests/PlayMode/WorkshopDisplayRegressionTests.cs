using System;
using System.Collections;
using System.IO;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using Object = UnityEngine.Object;

namespace FlightSim.Tests
{
    public class WorkshopDisplayRegressionTests
    {
        private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
        private GameObject _aircraft, _camera, _workshop, _backdrop;
        private float _timeScale;

        [SetUp]
        public void SetUp()
        {
            _timeScale = Time.timeScale;
            foreach (var ui in Object.FindObjectsOfType<WorkshopUI>())
                Object.DestroyImmediate(ui.gameObject);
        }

        [TearDown]
        public void TearDown()
        {
            if (_workshop != null) Object.DestroyImmediate(_workshop);
            if (_backdrop != null) Object.DestroyImmediate(_backdrop);
            if (_camera != null) Object.DestroyImmediate(_camera);
            if (_aircraft != null) Object.DestroyImmediate(_aircraft);
            Time.timeScale = _timeScale;
            typeof(WorkshopUI).GetProperty("IsOpen").SetValue(null, false);
        }

        [TestCase(1920, 1080)]
        [TestCase(1280, 720)]
        [TestCase(1280, 1024)]
        [TestCase(986, 555)]
        [TestCase(800, 600)]
        public void Entry_IsImmediatelyRightOfHelp_WithoutCoveringHeading(int width, int height)
        {
            float scale = HudLayout.Scale(width, height);
            Rect help = LayoutRect("HelpChipRect", scale);
            Rect entry = LayoutRect("WorkshopEntryRect", scale);
            Rect heading = LayoutRect("HeadingValueRect", width, scale);
            Assert.That(entry.x, Is.EqualTo(help.xMax + 8 * scale).Within(0.01f));
            Assert.That(entry.y, Is.EqualTo(help.y).Within(0.01f));
            Assert.That(entry.height, Is.EqualTo(help.height).Within(0.01f));
            Assert.LessOrEqual(entry.xMax, width);
            Assert.IsFalse(entry.Overlaps(help));
            Assert.IsFalse(entry.Overlaps(heading), "The relocated entry must not cover heading.");
        }

        [UnityTest]
        public IEnumerator OpenWorkshop_DoesNotThrow_WhenFlightStateIsTemporarilyUnavailable()
        {
            CreateUnavailableFlightScene();
            for (int i = 0; i < 4; i++) yield return null;
            Assert.IsTrue(WorkshopUI.IsOpen);
            LogAssert.NoUnexpectedReceived();
        }

        private WorkshopUI CreateUnavailableFlightScene()
        {
            _backdrop = new GameObject("WorkshopDisplayBackdrop");
            _backdrop.AddComponent<DisplayBackdrop>();
            _aircraft = new GameObject("WorkshopDisplayAircraft");
            var controller = _aircraft.AddComponent<AircraftController>();
            controller.Frozen = true;
            _aircraft.AddComponent<FlightHUD>();
            _camera = new GameObject("WorkshopDisplayCamera") { tag = "MainCamera" };
            _camera.AddComponent<Camera>();
            var chase = _camera.AddComponent<ChaseCamera>();
            chase.Target = _aircraft.transform;

            // Simulate non-serialized physics objects lost during an editor script reload.
            foreach (string field in new[] { "_plane", "_state", "_renderPrev", "_renderCurr" })
                typeof(AircraftController).GetField(field, Private).SetValue(controller, null);
            _workshop = new GameObject("WorkshopDisplayUI");
            var ui = _workshop.AddComponent<WorkshopUI>();
            typeof(WorkshopUI).GetField("_open", Private).SetValue(ui, true);
            typeof(WorkshopUI).GetProperty("IsOpen").SetValue(null, true);
            Time.timeScale = 0;
            return ui;
        }

        [UnityTest, Category("GuiCapture")]
        public IEnumerator OpenWorkshop_RendersBody_WhenFlightStateIsTemporarilyUnavailable()
        {
            if (Application.isBatchMode)
                Assert.Ignore("ScreenCapture requires an ordinary GUI Game view; run this case without -batchmode.");
            var ui = CreateUnavailableFlightScene();
            var gameView = UnityEditor.EditorWindow.GetWindow(
                typeof(UnityEditor.Editor).Assembly.GetType("UnityEditor.GameView"));
            gameView.Show();
            gameView.Focus();
            for (int i = 0; i < 8; i++)
            {
                gameView.Repaint();
                yield return null;
            }
            Assert.IsTrue(WorkshopUI.IsOpen);
            LogAssert.NoUnexpectedReceived();

            string path = Path.Combine(Application.temporaryCachePath,
                "workshop-display-" + Guid.NewGuid().ToString("N") + ".png");
            var capture = Type.GetType("UnityEngine.ScreenCapture, UnityEngine.ScreenCaptureModule", true);
            capture.GetMethod("CaptureScreenshot", new[] { typeof(string), typeof(int) })
                .Invoke(null, new object[] { path, 1 });
            float started = Time.realtimeSinceStartup;
            while (!File.Exists(path) && Time.realtimeSinceStartup - started < 10)
            {
                gameView.Repaint();
                yield return null;
            }
            Assert.IsTrue(File.Exists(path), "A real graphical screenshot must be captured.");
            var image = new Texture2D(2, 2);
            try
            {
                Assert.IsTrue(image.LoadImage(File.ReadAllBytes(path)));
                Rect window = (Rect)typeof(WorkshopUI).GetField("_winRect", Private).GetValue(ui);
                int lightPixels = 0;
                for (int y = 55; y < Math.Min(window.height - 65, 180); y += 4)
                    for (int x = 18; x < Math.Min(window.width - 18, 600); x += 4)
                    {
                        int px = Mathf.Clamp(Mathf.RoundToInt((window.x + x) / Screen.width * image.width), 0, image.width - 1);
                        int py = Mathf.Clamp(Mathf.RoundToInt((Screen.height - window.y - y) / Screen.height * image.height), 0, image.height - 1);
                        Color pixel = image.GetPixel(px, py);
                        // 浅色 Fluent 主题 (#F3F3F3): 亮像素才算盖住品红背板
                        // (主题假设: 深色主题改回 r/b < 0.4 的暗像素断言)
                        if (pixel.r > 0.8f && pixel.g > 0.8f && pixel.b > 0.8f) lightPixels++;
                    }
                Assert.Greater(lightPixels, 200, "The workshop body must paint over the magenta backdrop.");
                Debug.Log("[WorkshopDisplay] Visible body verified; screenshot: " + path);
            }
            finally { Object.DestroyImmediate(image); }
        }

        [UnityTest]
        public IEnumerator EntryRequest_IsAppliedByUpdate_AndDoesNotRequireReadyWorker()
        {
            _workshop = new GameObject("WorkshopEntryRequestUI");
            var ui = _workshop.AddComponent<WorkshopUI>();
            FieldInfo requested = typeof(WorkshopUI).GetField("_toggleRequested", Private);
            Assert.IsNotNull(requested, "Opening from OnGUI must defer its layout transition.");
            requested.SetValue(ui, true);
            yield return null;
            Assert.IsTrue(WorkshopUI.IsOpen);
            Assert.AreEqual(0, Time.timeScale);
            requested.SetValue(ui, true);
            yield return null;
            Assert.IsFalse(WorkshopUI.IsOpen);
        }

        private static Rect LayoutRect(string name, params object[] arguments)
        {
            MethodInfo method = typeof(HudLayout).GetMethod(name, BindingFlags.Public | BindingFlags.Static);
            Assert.IsNotNull(method, "Shared top-row layout is missing: " + name);
            return (Rect)method.Invoke(null, arguments);
        }

        private class DisplayBackdrop : MonoBehaviour
        {
            private Texture2D _texture;

            private void OnGUI()
            {
                if (_texture == null)
                {
                    _texture = new Texture2D(1, 1);
                    _texture.SetPixel(0, 0, Color.magenta);
                    _texture.Apply();
                }
                int depth = GUI.depth;
                Color color = GUI.color;
                GUI.depth = 1000;
                GUI.color = Color.white;
                GUI.DrawTexture(new Rect(0, 0, Screen.width, Screen.height), _texture);
                GUI.color = color;
                GUI.depth = depth;
            }

            private void OnDestroy()
            {
                if (_texture != null) Object.DestroyImmediate(_texture);
            }
        }
    }
}
