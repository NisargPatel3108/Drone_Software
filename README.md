# Minimal MAVLink GCS

A lightweight, high-performance Windows desktop application for drone control. 

## Features
- **Smart Scan & Connect**: Automated discovery of SITL and Hardware drones across UDP, TCP, and Serial ports.
- **Multi-Drone Support**: Live telemetry and switching between multiple active drones.
- **Force Arming**: Precision control with safety bypass capability (`21196`) for immediate arming.
- **MAVProxy Integration**: Automated backend routing to ensure compatibility with Mission Planner.

## Tech Stack
- **Languages**: C# (.NET Framework)
- **Architecture**: WinForms
- **Communication**: Custom MAVLink v1/v2 Parser

## How to Build
Simply run the Roslyn compiler (`csc.exe`) targeting the individual `.cs` files in the repository.

## GitHub
https://github.com/Prince-Tagadiya/Drone_Software
