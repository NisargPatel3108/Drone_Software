using System;
using MinimalGCS.Mavlink;

namespace MinimalGCS
{
    public static class SprayLogicEngine
    {
        // Relay value 1 = Pump OFF
        // Relay value 0 = Pump ON

        public static int EvaluatePumpState(DroneState state, Mission activeMission, float safeAlt, float geoRadius)
        {
            // Default to OFF (1)
            int targetRelay = 1;

            if (state == null) return targetRelay;

            // 1. HARD SAFETY BLOCKS
            if (!state.IsArmed) return 1; // Disarmed
            if (state.Mode != 3) return 1; // Not in AUTO mode (3)
            if (state.Mode == 6 || state.Mode == 9) return 1; // RTL or LAND
            if (state.GpsFixType < 3) return 1; // No GPS Lock
            
            // Altitude validation
            if (state.Alt < safeAlt) return 1; // Too low
            if (state.Alt > safeAlt + 15) return 1; // Too high (e.g. going RTL)

            // Geofence / Farm Area validation
            if (activeMission != null && activeMission.Waypoints.Count > 0)
            {
                // Calculate distance from Home (Takeoff/First WP)
                var home = activeMission.Waypoints.Find(w => w.Command == 22 || w.Index == 0) ?? activeMission.Waypoints[0];
                double distFromHome = Haversine(state.Lat, state.Lon, home.Lat, home.Lon);
                if (distFromHome > geoRadius) return 1; // Outside farm area
            }

            // 2. MISSION WAYPOINT LOGIC
            if (activeMission != null && state.CurrentWp > 0)
            {
                var currentWpTarget = activeMission.Waypoints.Find(w => w.Index == state.CurrentWp);
                if (currentWpTarget != null)
                {
                    // If target is a standard WAYPOINT (16) and not Takeoff/Land
                    if (currentWpTarget.Command == 16)
                    {
                        // Enable spray (0) when moving to a valid crop waypoint
                        targetRelay = 0;
                    }
                }
            }

            return targetRelay;
        }

        private static double Haversine(double lat1, double lon1, double lat2, double lon2)
        {
            double R = 6371000;
            double dLat = (lat2 - lat1) * Math.PI / 180;
            double dLon = (lon2 - lon1) * Math.PI / 180;
            double a = Math.Sin(dLat / 2) * Math.Sin(dLat / 2) + Math.Cos(lat1 * Math.PI / 180) * Math.Cos(lat2 * Math.PI / 180) * Math.Sin(dLon / 2) * Math.Sin(dLon / 2);
            return R * 2 * Math.Atan2(Math.Sqrt(a), Math.Sqrt(1 - a));
        }
    }
}
