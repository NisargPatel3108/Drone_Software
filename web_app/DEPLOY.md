# Agri Titan Web App + Relay (Recommended Deployment)

This `web_app` folder is **both**:

- a static web UI (served from `public/`)
- a WebSocket relay (`/ws`) that bridges **Mobile Web UI ⇄ Laptop GCS (MinimalGCS.exe)**

## Local run

```bash
cd web_app
npm install
npm start
```

Open: `http://localhost:8080`

## Cloud run (Render/Railway/etc.)

Deploy **this folder** as a Node web service:

- Start command: `npm start`
- Env:
  - `PORT` (provided by the platform)
  - `RELAY_PASSCODE` (your passcode)

After deploy:

- Your Web UI runs at `https://YOUR-SERVICE/`
- WebSocket URL is `wss://YOUR-SERVICE/ws`

## Configure Laptop GCS to connect to the relay

On the laptop running `MinimalGCS.exe`, edit:

- `relay_config.txt` (same folder as the EXE)

Put your relay WebSocket URL, for example:

`wss://YOUR-SERVICE/ws`

Then start `MinimalGCS.exe` and confirm the web UI shows **GCS connected**.

## If you host UI on static-only (Netlify)

Static hosting cannot run the relay. In that case:

1. Deploy the relay using Render/Railway/etc. (above)
2. In the web UI open **⚙ Server Settings** and set:
   - `wss://YOUR-SERVICE/ws`
