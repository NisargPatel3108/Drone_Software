using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace MinimalGCS
{
    public class WaypointItem
    {
        public int Index { get; set; }
        public byte CurrentWp { get; set; }
        public byte CoordFrame { get; set; }
        public int Command { get; set; }
        public float Param1 { get; set; }
        public float Param2 { get; set; }
        public float Param3 { get; set; }
        public float Param4 { get; set; }
        public double Lat { get; set; }
        public double Lon { get; set; }
        public float Alt { get; set; }
        public byte AutoContinue { get; set; }
    }

    public class Mission
    {
        public string Id { get; set; } = Guid.NewGuid().ToString("N");
        public string Name { get; set; } = "New Mission";
        public string Category { get; set; } = "Pesticide";
        public float FlightAltitude { get; set; } = 5.0f;
        public float DroneSpeed { get; set; } = 5.0f;
        public DateTime CreatedAt { get; set; } = DateTime.Now;
        public DateTime? LastUsed { get; set; }
        public List<WaypointItem> Waypoints { get; set; } = new List<WaypointItem>();

        public double TotalDistanceMeters => CalculateDistance();

        private double CalculateDistance()
        {
            var workWps = Waypoints.Where(w => w.Command == 16 && w.Lat != 0 && w.Lon != 0).ToList();
            double total = 0;
            for (int i = 1; i < workWps.Count; i++)
            {
                total += Haversine(workWps[i - 1].Lat, workWps[i - 1].Lon, workWps[i].Lat, workWps[i].Lon);
            }
            return total;
        }

        private double Haversine(double lat1, double lon1, double lat2, double lon2)
        {
            double R = 6371000;
            double dLat = (lat2 - lat1) * Math.PI / 180;
            double dLon = (lon2 - lon1) * Math.PI / 180;
            double a = Math.Sin(dLat / 2) * Math.Sin(dLat / 2) + Math.Cos(lat1 * Math.PI / 180) * Math.Cos(lat2 * Math.PI / 180) * Math.Sin(dLon / 2) * Math.Sin(dLon / 2);
            return R * 2 * Math.Atan2(Math.Sqrt(a), Math.Sqrt(1 - a));
        }
    }

    public static class MissionManager
    {
        private static readonly string MissionsDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Missions");

        public static void Init()
        {
            if (!Directory.Exists(MissionsDir)) Directory.CreateDirectory(MissionsDir);
        }

        public static List<Mission> GetMissions()
        {
            Init();
            var list = new List<Mission>();
            foreach (var file in Directory.GetFiles(MissionsDir, "*.json"))
            {
                try
                {
                    var json = File.ReadAllText(file);
                    var mission = System.Text.Json.JsonSerializer.Deserialize<Mission>(json);
                    if (mission != null) list.Add(mission);
                }
                catch { }
            }
            return list.OrderByDescending(m => m.CreatedAt).ToList();
        }

        public static void SaveMission(Mission mission)
        {
            Init();
            var path = Path.Combine(MissionsDir, $"{mission.Id}.json");
            var json = System.Text.Json.JsonSerializer.Serialize(mission);
            File.WriteAllText(path, json);
        }

        public static void DeleteMission(string id)
        {
            var path = Path.Combine(MissionsDir, $"{id}.json");
            if (File.Exists(path)) File.Delete(path);
        }

        public static List<WaypointItem> ParseWaypointFile(string filePath)
        {
            var lines = File.ReadAllLines(filePath);
            var items = new List<WaypointItem>();

            for (int i = 0; i < lines.Length; i++)
            {
                var line = lines[i].Trim();
                if (string.IsNullOrEmpty(line) || line.StartsWith("#") || line.StartsWith("QGC"))
                    continue;

                var parts = line.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length >= 12)
                {
                    try
                    {
                        items.Add(new WaypointItem
                        {
                            Index = int.Parse(parts[0]),
                            CurrentWp = byte.Parse(parts[1]),
                            CoordFrame = byte.Parse(parts[2]),
                            Command = int.Parse(parts[3]),
                            Param1 = float.Parse(parts[4], System.Globalization.CultureInfo.InvariantCulture),
                            Param2 = float.Parse(parts[5], System.Globalization.CultureInfo.InvariantCulture),
                            Param3 = float.Parse(parts[6], System.Globalization.CultureInfo.InvariantCulture),
                            Param4 = float.Parse(parts[7], System.Globalization.CultureInfo.InvariantCulture),
                            Lat = double.Parse(parts[8], System.Globalization.CultureInfo.InvariantCulture),
                            Lon = double.Parse(parts[9], System.Globalization.CultureInfo.InvariantCulture),
                            Alt = float.Parse(parts[10], System.Globalization.CultureInfo.InvariantCulture),
                            AutoContinue = byte.Parse(parts[11])
                        });
                    }
                    catch { }
                }
            }
            return items;
        }
    }
}
