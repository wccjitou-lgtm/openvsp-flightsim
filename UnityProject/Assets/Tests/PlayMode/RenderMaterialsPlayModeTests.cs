using FlightSim;
using NUnit.Framework;
using UnityEngine;

namespace FlightSim.Tests
{
    /// <summary>
    /// RenderMaterials 管线材质工厂测试 (渲染升级 R1)。
    /// 三种管线分支各自有断言; 当前工程状态构造不了的分支用 Assert.Ignore 说明
    /// (切换管线需要改项目设置, 不应在测试里动)。
    /// </summary>
    public class RenderMaterialsPlayModeTests
    {
        [Test]
        public void RenderMaterials_Create_NeverReturnsErrorShader()
        {
            var mat = RenderMaterials.Create(new Color(0.5f, 0.6f, 0.7f), 0.3f);
            Assert.IsNotNull(mat.shader, "shader 解析失败 (FindLitShader 兜底链全部落空)");
            StringAssert.DoesNotContain("InternalErrorShader", mat.shader.name);
        }

        [Test]
        public void RenderMaterials_BuiltInBranch_UsesStandardShader()
        {
            if (RenderMaterials.CurrentPipeline() != RenderMaterials.Pipeline.BuiltIn)
                Assert.Ignore("当前工程为非 Built-in 管线, Built-in 分支需改项目设置才能构造");
            Assert.AreEqual("Standard", RenderMaterials.Create(Color.white).shader.name);
        }

        [Test]
        public void RenderMaterials_HdrpBranch_UsesHdrpLitShader()
        {
            if (RenderMaterials.CurrentPipeline() != RenderMaterials.Pipeline.Hdrp)
                Assert.Ignore("当前工程为非 HDRP 管线, HDRP 分支需改项目设置才能构造");
            var mat = RenderMaterials.Create(Color.white);
            Assert.AreEqual("HDRP/Lit", mat.shader.name);
            // [MainColor] 特性应把 mat.color 映射到 _BaseColor (R1 设计前提)
            var c = new Color(0.25f, 0.5f, 0.75f);
            mat.color = c;
            Assert.AreEqual(c.r, mat.GetColor("_BaseColor").r, 0.002f, "mat.color 未映射 _BaseColor");
        }

        [Test]
        public void RenderMaterials_UrpBranch_UsesUrpLitShader()
        {
            if (RenderMaterials.CurrentPipeline() != RenderMaterials.Pipeline.Urp)
                Assert.Ignore("当前工程为非 URP 管线, URP 分支需改项目设置才能构造");
            Assert.AreEqual("Universal Render Pipeline/Lit", RenderMaterials.Create(Color.white).shader.name);
        }

        [Test]
        public void RenderMaterials_ColorRoundTripsThroughMainColor()
        {
            var c = new Color(0.25f, 0.5f, 0.75f, 1f);
            var mat = RenderMaterials.Create(c);
            Assert.AreEqual(c.r, mat.color.r, 0.002f);
            Assert.AreEqual(c.g, mat.color.g, 0.002f);
            Assert.AreEqual(c.b, mat.color.b, 0.002f);
        }

        [Test]
        public void RenderMaterials_SmoothnessMapsToPipelineProperty()
        {
            var mat = RenderMaterials.Create(Color.white, 0.42f);
            string prop = RenderMaterials.CurrentPipeline() == RenderMaterials.Pipeline.BuiltIn
                ? "_Glossiness" : "_Smoothness";
            Assert.IsTrue(mat.HasProperty(prop), "缺少光滑度属性: " + prop);
            Assert.AreEqual(0.42f, mat.GetFloat(prop), 0.005f);
        }
    }
}
