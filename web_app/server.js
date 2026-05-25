const express = require('express');
const http = require('http');
const WebSocket = require('ws');
const path = require('path');

const app = express();
// Enable CORS for all routes to allow web app connections from any origin
app.use((req, res, next) => {
  res.setHeader('Access-Control-Allow-Origin', '*');
  res.setHeader('Access-Control-Allow-Methods', 'GET,POST,OPTIONS');
  res.setHeader('Access-Control-Allow-Headers', 'Content-Type');
  next();
});
const port = process.env.PORT || 8080;
const PASSCODE = process.env.RELAY_PASSCODE || "12345";

// Cache for last telemetry state
let lastTelemetry = null;
let lastGcsSeenAt = null;

// Categorized clients
let gcsSocket = null;
const mobileSockets = new Set();

function isGcsConnected() {
  return !!gcsSocket && gcsSocket.readyState === WebSocket.OPEN;
}

// Serve static UI dashboard files
app.use(express.static(path.join(__dirname, 'public')));

// Simple status endpoint for sanity check
app.get('/status', (req, res) => {
  res.json({
    ok: true,
    gcsConnected: isGcsConnected(),
    activeMobileCount: mobileSockets.size,
    hasTelemetry: !!lastTelemetry,
    lastGcsSeenAt
  });
});

const server = http.createServer(app);

// Integrate WebSocket Server
server.on('upgrade', (request, socket, head) => {
  const pathname = new URL(request.url, `http://${request.headers.host}`).pathname;

  if (pathname === '/ws') {
    wss.handleUpgrade(request, socket, head, (ws) => {
      wss.emit('connection', ws, request);
    });
  } else {
    socket.destroy();
  }
});

const wss = new WebSocket.Server({ noServer: true });

wss.on('connection', (ws, request) => {
  console.log('New connection established.');
  let clientType = null;
  let isAuthenticated = false;

  ws.on('message', (message) => {
    try {
      const data = JSON.parse(message.toString());

      // 1. REGISTRATION PHASE
      if (data.type === 'register') {
        if (data.client === 'gcs') {
          if (gcsSocket && gcsSocket !== ws && gcsSocket.readyState === WebSocket.OPEN) {
            try { gcsSocket.close(1000, 'Replaced by a new GCS connection'); } catch { }
          }

          gcsSocket = ws;
          clientType = 'gcs';
          isAuthenticated = true;
          lastGcsSeenAt = new Date().toISOString();
          console.log('C# GCS Connected and Registered.');

          ws.send(JSON.stringify({ type: 'registered', client: 'gcs' }));
          
          broadcastToMobiles(JSON.stringify({ type: 'gcs_status', connected: true }));
        } 
        else if (data.client === 'mobile') {
          // Verify Passcode
          if (data.passcode === PASSCODE) {
            mobileSockets.add(ws);
            clientType = 'mobile';
            isAuthenticated = true;
            console.log('Mobile Client Authenticated & Registered.');
            
            // Instantly send connection status and last telemetry if available
            ws.send(JSON.stringify({ type: 'gcs_status', connected: isGcsConnected() }));
            if (lastTelemetry) {
              ws.send(JSON.stringify(lastTelemetry));
            }
          } else {
            console.log('Mobile Client connection rejected: Invalid Passcode.');
            ws.send(JSON.stringify({ type: 'error', message: 'Auth Failed: Invalid Passcode' }));
            ws.close();
          }
        }
        return;
      }

      // 2. BIDIRECTIONAL ROUTING PHASE
      if (clientType === 'gcs') {
        lastGcsSeenAt = new Date().toISOString();
        // Cache last telemetry state for new mobile connections
        if (data.type === 'telemetry') {
          lastTelemetry = data;
        }
        // Generically relay all GCS messages (telemetry, missions_list, status updates) to mobiles
        broadcastToMobiles(JSON.stringify(data));
      } 
      else if (clientType === 'mobile' && isAuthenticated) {
        // Generically relay all authenticated mobile client messages (commands, load_mission, request_missions) to GCS
        console.log(`Relaying Mobile Client Message: ${data.type}`);
        if (isGcsConnected()) {
          gcsSocket.send(JSON.stringify(data));
        } else {
          ws.send(JSON.stringify({ type: 'error', message: 'GCS Unavailable. Verify desktop application is running.' }));
        }
      }
    } catch (err) {
      console.error('Error handling message:', err.message);
    }
  });

  ws.on('close', () => {
    if (clientType === 'gcs') {
      console.log('C# GCS Client Disconnected.');
      if (gcsSocket === ws) {
        gcsSocket = null;
        lastTelemetry = null;
        broadcastToMobiles(JSON.stringify({ type: 'gcs_status', connected: false }));
      }
    } else if (clientType === 'mobile') {
      console.log('Mobile Client Disconnected.');
      mobileSockets.delete(ws);
    }
  });
});

// Helper: Broadcast to all connected mobile clients
function broadcastToMobiles(msg) {
  for (const client of mobileSockets) {
    if (client.readyState === WebSocket.OPEN) {
      client.send(msg);
    } else {
      mobileSockets.delete(client);
    }
  }
}

server.listen(port, () => {
  console.log(`====================================================`);
  console.log(`AGRI TITAN REMOTE SERVER RUNNING`);
  console.log(`Local Access: http://localhost:${port}`);
  console.log(`Default Passcode: ${PASSCODE}`);
  console.log(`====================================================`);
});
