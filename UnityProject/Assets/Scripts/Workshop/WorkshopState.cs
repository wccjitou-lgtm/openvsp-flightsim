using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace FlightSim
{
    /// <summary>
    /// 机型工坊 W1: 工程状态数据 (6 槽位 / 几何清单 / 部件库 / 质量结果)。
    /// 纯数据类, 不碰 worker 进程; UI 负责驱动同步。
    /// workshop.json (提示词 §9, 槽位用数组表达 — JsonUtility 无字典):
    ///   {"schemaVersion":1,"name":"X","slots":[{"slot":"wing1","geom":"MainWing","color":"#4AA3FF"}]}
    /// </summary>
    [Serializable]
    public class WorkshopState
    {
        public const int SchemaVersion = 1;

        [Serializable]
        public class SlotInfo
        {
            public string key;          // fuselage/wing1/wing2/htail/vtail/ballast
            public string geom;         // worker 侧几何名 (vsp3 重开后 id 会变, 按名恢复)
            [NonSerialized] public string geomId;
            [NonSerialized] public string typeName;
            [NonSerialized] public int meshTris;
            public string color;        // "#RRGGBB" (W1 预设色; W2 取色器)
        }

        [Serializable]
        public class GeomInfo
        {
            public string id;
            public string name;
            public string type;      // Wing/Fuselage/Pod/...
            public bool hidden;
            public string slot;      // null = 未分配
            public SubSurfInfo[] subsurfs;
        }

        [Serializable]
        public class SubSurfInfo
        {
            public string id;
            public string name;
            public int type;         // 3 = SS_CONTROL
            public float eta_start;  // 以下仅 SS_CONTROL 有 (W2 操纵面 3D 高亮用)
            public float eta_end;
            public float len_c;
        }

        [Serializable]
        public class LibraryItem
        {
            public string type;      // fuselage/wing/ballast
            public string name;      // 显示名 (中文)
            public string path;
        }

        [Serializable]
        public class MassResult
        {
            public float massKg;
            public Vector3 cgOvsp;   // OpenVSP 体轴 (x 后, y 右, z 上)
            public int numComps;
            public PerGeom[] perGeom = new PerGeom[0];   // 部件质量 (换机副本烘 PointMass 用)

            [Serializable]
            public class PerGeom
            {
                public string name;
                public float massKg;
            }
        }

        public string projectName = "Unnamed";
        public string projectPath = "";     // 空 = 未保存过
        public bool dirty;
        public List<GeomInfo> geoms = new List<GeomInfo>();
        public List<LibraryItem> library = new List<LibraryItem>();
        public MassResult mass = new MassResult();

        // 槽位顺序与 worker/workshop_common.SLOTS 一致
        public List<SlotInfo> slots = new List<SlotInfo>
        {
            new SlotInfo { key = "fuselage", color = "#C8CDD4" },
            new SlotInfo { key = "wing1",    color = "#4AA3FF" },
            new SlotInfo { key = "wing2",    color = "#7FD4A8" },
            new SlotInfo { key = "htail",    color = "#F2C14E" },
            new SlotInfo { key = "vtail",    color = "#E8788A" },
            new SlotInfo { key = "ballast",  color = "#9B8BB4" },
        };

        public SlotInfo Slot(string key)
        {
            return slots.Find(s => s.key == key);
        }

        public GeomInfo GeomById(string id)
        {
            return geoms.Find(g => g.id == id);
        }

        public GeomInfo GeomByName(string name)
        {
            return geoms.Find(g => g.name == name);
        }

        /// <summary>槽位允许的几何类型 (与 workshop_common.SLOT_TYPES 一致)。</summary>
        public static bool TypeFitsSlot(string slot, string geomType)
        {
            switch (slot)
            {
                case "fuselage": return geomType == "Fuselage" || geomType == "Pod";
                case "wing1":
                case "wing2":
                case "htail":
                case "vtail": return geomType == "Wing";
                case "ballast": return geomType == "Pod";
                default: return false;
            }
        }

        // ---------- worker 响应装载 ----------
        [Serializable]
        private class GeomsData { public GeomInfo[] geoms; }
        [Serializable]
        private class LibData { public LibraryItem[] items; }
        [Serializable]
        private class MassData { public float mass_kg; public float[] cg_m; public int num_comps; }
        [Serializable]
        private class MeshesData
        {
            [Serializable]
            public class MeshInfo { public string path; public float tris; public string name; }
            public MeshInfo[] _ignored;  // 占位: 实际按槽 key 索引, 见 LoadMeshes
        }

        /// <summary>解析 list_geoms/open 的 data JSON, 更新几何清单与槽位 geomId。</summary>
        public void LoadGeoms(string dataJson)
        {
            var d = JsonUtility.FromJson<GeomsData>(dataJson);
            geoms = new List<GeomInfo>(d.geoms ?? new GeomInfo[0]);
            foreach (var g in geoms)
                // U17 关键修复: JsonUtility 把 JSON null 字符串解析成 "" (不是 null),
                // 导致未分配/隐藏部件的 slot="" 被当成"已分配"而过滤出下拉框
                // (用户实测: 新建替代/选「无」后原部件从下拉消失)
                if (g.slot == "") g.slot = null;
            foreach (var s in slots)
            {
                var g = geoms.Find(x => x.slot == s.key);
                s.geomId = g != null ? g.id : null;
                s.geom = g != null ? g.name : null;
                s.typeName = g != null ? g.type : null;
            }
        }

        public void LoadLibrary(string dataJson)
        {
            var d = JsonUtility.FromJson<LibData>(dataJson);
            library = new List<LibraryItem>(d.items ?? new LibraryItem[0]);
        }

        public void LoadMass(string dataJson)
        {
            var d = JsonUtility.FromJson<MassData>(dataJson);
            mass.massKg = d.mass_kg;
            mass.cgOvsp = d.cg_m != null && d.cg_m.Length >= 3
                ? new Vector3(d.cg_m[0], d.cg_m[1], d.cg_m[2]) : Vector3.zero;
            mass.numComps = d.num_comps;
            var list = new List<MassResult.PerGeom>();
            string perRaw = WorkshopJson.Raw(dataJson, "per_geom");
            if (perRaw != null)
            {
                // per_geom 是 {"部件名": {"mass_kg": ..}} 字典 (U06: 原按数组解析恒空,
                // 换机 mass_map 因此恒空 → Shell 质量丢失 → Ixx≈0 → 地面 NaN)
                foreach (var kv in WorkshopJson.SplitObject(perRaw))
                {
                    float kg = WorkshopJson.Num(kv.Value, "mass_kg", float.NaN);
                    if (!string.IsNullOrEmpty(kv.Key) && !float.IsNaN(kg))
                        list.Add(new MassResult.PerGeom { name = kv.Key, massKg = kg });
                }
            }
            mass.perGeom = list.ToArray();
        }

        /// <summary>从 export_meshes 的 data JSON 取某槽的 STL 路径 (手写抽取, 槽 key 动态)。</summary>
        public static string MeshPathFor(string dataJson, string slotKey)
        {
            string meshes = WorkshopJson.Raw(dataJson, "meshes");
            if (meshes == null) return null;
            // slotKey 是普通字段名 (拼引号的活交给 FindTopLevel), 不带引号传入
            string slotObj = WorkshopJson.Raw(meshes, slotKey);
            if (slotObj == null) return null;
            return WorkshopJson.Str(slotObj, "path");
        }

        // ---------- workshop.json ----------
        [Serializable]
        private class SaveRoot
        {
            public int schemaVersion;
            public string name;
            public SlotSave[] slots;
            public string aeroJson;    // 栏②面板字段 (W3, JSON 字符串直存)
            public string[] excluded;  // R06: 排除几何名单 (隐藏 ⇔ 排除, 权威在此)
        }

        [Serializable]
        private class SlotSave
        {
            public string slot;
            public string geom;
            public string color;
        }

        /// <summary>把槽位分配写到 vsp3 同名 .workshop.json (提示词 §5.1)。</summary>
        public void SaveSidecar(string vsp3Path, string aeroJson = null)
        {
            var ex = new List<string>();
            foreach (var g in geoms)
                if (g.hidden) ex.Add(g.name);
            var root = new SaveRoot
            {
                schemaVersion = SchemaVersion,
                name = Path.GetFileNameWithoutExtension(vsp3Path),
                slots = new SlotSave[slots.Count],
                aeroJson = aeroJson,
                excluded = ex.ToArray(),
            };
            for (int i = 0; i < slots.Count; i++)
                root.slots[i] = new SlotSave
                {
                    slot = slots[i].key,
                    geom = slots[i].geom,     // 空/「无」= null
                    color = slots[i].color,
                };
            string p = Path.ChangeExtension(vsp3Path, ".workshop.json");
            File.WriteAllText(p, JsonUtility.ToJson(root, true), System.Text.Encoding.UTF8);
        }

        /// <summary>读 .workshop.json 恢复槽位分配; out: aeroJson / excluded 名单。
        /// 无文件返回 false。R06: excluded 为排除几何权威名单。</summary>
        public bool LoadSidecar(string vsp3Path, out string aeroJson, out string[] excluded)
        {
            aeroJson = null;
            excluded = null;
            string p = Path.ChangeExtension(vsp3Path, ".workshop.json");
            if (!File.Exists(p)) return false;
            try
            {
                var root = JsonUtility.FromJson<SaveRoot>(
                    File.ReadAllText(p, System.Text.Encoding.UTF8));
                if (root == null || root.slots == null) return false;
                foreach (var sv in root.slots)
                {
                    var s = Slot(sv.slot);
                    if (s != null)
                    {
                        s.geom = sv.geom;
                        if (!string.IsNullOrEmpty(sv.color)) s.color = sv.color;
                    }
                }
                aeroJson = root.aeroJson;
                excluded = root.excluded;
                return true;
            }
            catch (System.Exception e)
            {
                UnityEngine.Debug.LogWarning("[WorkshopState] workshop.json 读取失败: " + e.Message);
                return false;
            }
        }
    }
}
