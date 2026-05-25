const express = require('express');
const http = require('http');
const WebSocket = require('ws');
const path = require('path');

const app = express();
const port = process.env.PORT || 8080;
const PASSCODE = process.env.RELAY_PASSCODE || "12345";

// Cache for last telemetry state
let lastTelemetry = null;

// Categorized clients
let gcsSocket = null;
const mobileSockets = new Set();

// Serve static UI dashboard files
app.use(express.static(path.join(__dirname, 'public')));

// Simple status endpoint for sanity check
app.get('/status', (req, res) => {
  res.json({
    gcsConnected: !!gcsSocket,
    activeMobileCount: mobileSockets.size,
    hasTelemetry: !!lastTelemetry
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
          // Register GCS (No password required for local GCS client by default, or verify token)
          gcsSocket = ws;
          clientType = 'gcs';
          console.log('C# GCS Connected and Registered.');
          
          // Notify mobile clients GCS is online
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
            ws.send(JSON.stringify({ type: 'gcs_status', connected: !!gcsSocket }));
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
        // GCS sends telemetry -> Cache and forward to all mobile devices
        if (data.type === 'telemetry') {
          lastTelemetry = data;
          const msgString = JSON.stringify(data);
          broadcastToMobiles(msgString);
        }
      } 
      else if (clientType === 'mobile' && isAuthenticated) {
        // Mobile sends a command -> Forward ONLY to the C# GCS client
        if (data.type === 'command') {
          console.log(`Forwarding Mobile Command: ${data.command}`);
          if (gcsSocket && gcsSocket.readyState === WebSocket.OPEN) {
            gcsSocket.send(JSON.stringify(data));
          } else {
            ws.send(JSON.stringify({ type: 'error', message: 'GCS Unavailable. Verify desktop application is running.' }));
          }
        }
      }
    } catch (err) {
      console.error('Error handling message:', err.message);
    }
  });

  ws.on('close', () => {
    if (clientType === 'gcs') {
      console.log('C# GCS Client Disconnected.');
      gcsSocket = null;
      lastTelemetry = null;
      broadcastToMobiles(JSON.stringify({ type: 'gcs_status', connected: false }));
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
