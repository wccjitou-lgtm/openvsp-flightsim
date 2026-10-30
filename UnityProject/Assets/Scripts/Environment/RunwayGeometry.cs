using UnityEngine;

namespace FlightSim
{
    /// <summary>
    /// 跑道几何 (只读描述, S1)。全部使用 NED 水平坐标 (x=北, y=东), 与 AircraftState.Position
    /// 同系, 供 ModePilot 降落等逻辑读取, 取代原先硬编码的"跑道朝北、入口在 Home 南 80m"。
    /// HeadingRad 为跑道使用方向的 NED 航向 (0=朝北); along 沿跑道方向自入口 (Threshold) 起算,
    /// cross 以跑道方向的右侧为正。
    /// </summary>
    public sealed class RunwayGeometry
    {
        /// <summary>入口 (着陆端跑道起点) 中线点, NED (北, 东)。</summary>
        public Vector2 ThresholdNed;
        public float HeadingRad;
        public float LengthM;
        public float WidthM;
        /// <summary>跑道面海拔 (米, 向上为正; 与 AircraftState.Altitude 同基准)。</summary>
        public float ElevationM;
        /// <summary>瞄准点距入口的距离 (米)。</summary>
        public float TouchdownDistM;
        /// <summary>接地区终点距入口的距离 (米); 接地点应落在 [0, 此值] 内。</summary>
        public float TouchdownZoneEndM;

        public Vector2 Forward => new Vector2(Mathf.Cos(HeadingRad), Mathf.Sin(HeadingRad));
        public Vector2 Right => new Vector2(-Mathf.Sin(HeadingRad), Mathf.Cos(HeadingRad));

        /// <summary>posNed 相对入口: along (沿跑道, 前进为正), cross (右侧为正)。</summary>
        public void AlongCross(Vector2 posNed, out float along, out float cross)
        {
            Vector2 d = posNed - ThresholdNed;
            Vector2 f = Forward, r = Right;
            along = d.x * f.x + d.y * f.y;
            cross = d.x * r.x + d.y * r.y;
        }

        /// <summary>跑道坐标 (along, cross) → NED 水平点。</summary>
        public Vector2 PointAlong(float along, float cross = 0f)
        {
            return ThresholdNed + Forward * along + Right * cross;
        }
    }
}
