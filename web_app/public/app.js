// STATE & CONFIGURATION
let socket = null;
let reconnectTimer = null;
let relayStatusTimer = null;
let currentWsUrl = "";
let lastGcsState = false;
let passcode = localStorage.getItem('agri_titan_passcode') || "";
// Default server URL behavior:
// - If the UI is hosted *together* with the relay (recommended), leave this empty to use same-origin `/ws`.
// - If the UI is hosted as static-only (e.g. Netlify), set a full relay URL in Server Settings (or keep the legacy Render default).
const legacyCloudRelay = "wss://agri-titan-relay.onrender.com";
const isStaticWebHost = window.location.hostname.includes("netlify.app") || window.location.hostname.includes("github.io");
const inferredDefaultRelay = isStaticWebHost ? legacyCloudRelay : "";
let customServerUrl = getInitialServerUrl();
let isArmedGlobal = false;
let droneMarker = null;
let mapInstance = null;

// PWA MAP VISUALIZATION STATE
let activeMissionsList = [];
let currentActiveMission = null;
let mobileWaypointMarkers = [];
let mobilePathLine = null;
let mobileCompletedLine = null;
let mobileRunningLine = null;
let mobileLandingMarker = null;

// Map configuration
const MAP_ZOOM = 17;

// DOM Elements
const authScreen = document.getElementById('auth-screen');
const appScreen = document.getElementById('app-screen');
const passcodeInput = document.getElementById('passcode-input');
const authBtn = document.getElementById('auth-btn');
const authError = document.getElementById('auth-error');
const logoutBtn = document.getElementById('logout-btn');

// Server configuration DOM Elements
const toggleSettingsBtn = document.getElementById('toggle-settings-btn');
const settingsPanel = document.getElementById('settings-panel');
const serverUrlInput = document.getElementById('server-url-input');
const toggleArrow = document.getElementById('toggle-arrow');

const indServer = document.getElementById('ind-server');
const indGcs = document.getElementById('ind-gcs');
const connectionWarning = document.getElementById('connection-warning');

// Telemetry Elements
const elDroneId = document.getElementById('val-drone-id');
const elFlightMode = document.getElementById('val-flight-mode');
const elMotorStatus = document.getElementById('val-motor-status');
const elBattery = document.getElementById('val-battery');
const elVoltage = document.getElementById('val-voltage');
const elAlt = document.getElementById('val-alt');
const elMaxAlt = document.getElementById('val-max-alt');
const elSpeed = document.getElementById('val-speed');
const elHeading = document.getElementById('val-heading');
const elGpsStatus = document.getElementById('val-gps-status');
const elSats = document.getElementById('val-sats');
const elHdop = document.getElementById('val-hdop');
const elPumpToggle = document.getElementById('pump-toggle');
const elPumpSubText = document.getElementById('pump-sub-text');
const logList = document.getElementById('log-list');

// Safety Slider Elements
const slideArm = document.getElementById('slide-arm-container');
const slideMission = document.getElementById('slide-mission-container');
const slideDisarm = document.getElementById('slide-disarm-container');

// NEW UI HUD Elements
const elBattFill = document.getElementById('batt-fill-bar');
const elFlightOpsBadge = document.getElementById('flight-ops-badge');
const elMapProgressHud = document.getElementById('map-progress-hud');
const elMapProgressPct = document.getElementById('map-progress-pct');
const elMapProgressBar = document.getElementById('map-progress-bar');
const elMapProgressDetail = document.getElementById('map-progress-detail');

// 1. INITIALIZATION & AUTHENTICATION
if (passcode) {
  showAppScreen();
}

// Pre-fill Server URL field if available in local cache
if (toggleSettingsBtn && settingsPanel) {
  serverUrlInput.value = customServerUrl;
  
  toggleSettingsBtn.addEventListener('click', (e) => {
    e.preventDefault();
    const isActive = settingsPanel.classList.toggle('active');
    toggleArrow.textContent = isActive ? "▲" : "▼";
  });
}

authBtn.addEventListener('click', handleAuthSubmit);
passcodeInput.addEventListener('keypress', (e) => {
  if (e.key === 'Enter') handleAuthSubmit();
});

logoutBtn.addEventListener('click', () => {
  localStorage.removeItem('agri_titan_passcode');
  passcode = "";
  if (socket) socket.close();
  authScreen.classList.add('active');
  appScreen.classList.remove('active');
});

function handleAuthSubmit() {
  const value = passcodeInput.value.trim();
  if (!value) {
    authError.textContent = "PLEASE ENTER A PASSCODE";
    return;
  }
  passcode = value;
  
  // Save custom server URL if configured
  if (serverUrlInput) {
    customServerUrl = serverUrlInput.value.trim();
    localStorage.setItem('agri_titan_server_url', customServerUrl);
  }
  
  showAppScreen();
}

function showAppScreen() {
  authScreen.classList.remove('active');
  appScreen.classList.add('active');
  localStorage.setItem('agri_titan_passcode', passcode);
  
  // Initialize Leaflet Map
  initMap();
  
  // Establish WebSocket Connection
  connectWebSocket();
}

// 2. LEAFLET MAP SERVICE
function initMap() {
  if (mapInstance) return;
  
  // Center locally first (will update dynamically when drone locks GPS)
  mapInstance = L.map('map', {
    zoomControl: false,
    attributionControl: false
  }).setView([20.5937, 78.9629], 5); // Default to India center
  
  L.tileLayer('https://mt1.google.com/vt/lyrs=y&x={x}&y={y}&z={z}', {
    maxZoom: 22,
    maxNativeZoom: 20
  }).addTo(mapInstance);
  
  // Custom rotating hexacopter marker icon using raw SVG
  const droneSvg = `
    <svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 100 100" width="40" height="40">
      <circle cx="50" cy="50" r="12" fill="#10b8a6" stroke="#fff" stroke-width="2" />
      <line x1="50" y1="50" x2="50" y2="15" stroke="#10b8a6" stroke-width="4" />
      <line x1="50" y1="50" x2="85" y2="30" stroke="#94a3b8" stroke-width="3" />
      <line x1="50" y1="50" x2="85" y2="70" stroke="#94a3b8" stroke-width="3" />
      <line x1="50" y1="50" x2="50" y2="85" stroke="#94a3b8" stroke-width="3" />
      <line x1="50" y1="50" x2="15" y2="70" stroke="#94a3b8" stroke-width="3" />
      <line x1="50" y1="50" x2="15" y2="30" stroke="#94a3b8" stroke-width="3" />
      <circle cx="50" cy="15" r="5" fill="#ef4444" /> <!-- Front Indicator (Red) -->
    </svg>
  `;
  
  const droneIcon = L.divIcon({
    html: `<div id="drone-marker-div" style="transform: rotate(0deg); transform-origin: 50% 50%; transition: transform 0.2s;">${droneSvg}</div>`,
    className: 'custom-drone-icon',
    iconSize: [40, 40],
    iconAnchor: [20, 20]
  });
  
  droneMarker = L.marker([20.5937, 78.9629], { icon: droneIcon }).addTo(mapInstance);
  mobilePathLine = L.polyline([], { color: '#fd7e14', weight: 3, opacity: 0.7, dashArray: '8 4' }).addTo(mapInstance);
  mobileCompletedLine = L.polyline([], { color: '#10b8a6', weight: 5, opacity: 0.95, lineCap: 'round', lineJoin: 'round' }).addTo(mapInstance);
  mobileRunningLine = L.polyline([], { className: 'running-path-animation', color: '#ffc107', weight: 4, opacity: 0.9 }).addTo(mapInstance);
}

function drawMissionOnMobileMap(mission) {
  if (!mapInstance || !mission || !mission.waypoints) return;
  
  // Clear existing mobile waypoint markers
  mobileWaypointMarkers.forEach(m => mapInstance.removeLayer(m));
  mobileWaypointMarkers = [];
  if (mobileLandingMarker) {
    mapInstance.removeLayer(mobileLandingMarker);
    mobileLandingMarker = null;
  }
  
  const points = [];
  mission.waypoints.forEach(wp => {
    if (wp.lat === 0 && wp.lon === 0) return;
    points.push([wp.lat, wp.lon]);
    
    const isTakeoff = (wp.command === 22 || wp.index === 1);
    const isLand = (wp.command === 21 || wp.command === 20);
    const label = wp.index === 0 ? 'H' : (isTakeoff ? 'T' : (isLand ? 'L' : wp.index));
    const bg = wp.index === 0 ? '#10b8a6' : (isTakeoff ? '#3b82f6' : (isLand ? '#ef4444' : '#f59e0b'));
    
    const icon = L.divIcon({
      html: `<div style="background:${bg};color:#fff;border:2px solid #fff;border-radius:50%;width:22px;height:22px;display:flex;align-items:center;justify-content:center;font-weight:700;font-family:var(--font-mono);font-size:10px;box-shadow:0 2px 6px rgba(0,0,0,.5);">${label}</div>`,
      className: 'leaflet-div-icon',
      iconSize: [22, 22],
      iconAnchor: [11, 11]
    });
    
    const m = L.marker([wp.lat, wp.lon], { icon: icon }).addTo(mapInstance);
    mobileWaypointMarkers.push(m);
  });
  
  mobilePathLine.setLatLngs(points);
  mobileCompletedLine.setLatLngs([]);
  mobileRunningLine.setLatLngs([]);
  
  if (mobileWaypointMarkers.length > 1) {
    mapInstance.fitBounds(new L.featureGroup(mobileWaypointMarkers).getBounds().pad(0.15));
  }
}

function updateMobileMissionProgress(currentWp, totalWp, modeName, droneLat, droneLon) {
  if (!currentActiveMission || !currentActiveMission.waypoints) return;
  
  const wps = currentActiveMission.waypoints.filter(w => w.lat !== 0 && w.lon !== 0);
  if (wps.length === 0) return;
  
  const completedCoords = [];
  const remainingCoords = [];
  
  let activeSegIdx = -1;
  for (let i = 0; i < wps.length; i++) {
    if (wps[i].index === currentWp) {
      activeSegIdx = i;
      break;
    }
  }
  
  if (activeSegIdx >= 0) {
    for (let i = 0; i < activeSegIdx; i++) {
      completedCoords.push([wps[i].lat, wps[i].lon]);
    }
    completedCoords.push([droneLat, droneLon]);
    
    remainingCoords.push([droneLat, droneLon]);
    for (let i = activeSegIdx; i < wps.length; i++) {
      remainingCoords.push([wps[i].lat, wps[i].lon]);
    }
  } else {
    if (currentWp === 0) {
      wps.forEach(wp => remainingCoords.push([wp.lat, wp.lon]));
    } else {
      wps.forEach(wp => completedCoords.push([wp.lat, wp.lon]));
    }
  }
  
  mobileCompletedLine.setLatLngs(completedCoords);
  mobilePathLine.setLatLngs(remainingCoords);
  
  const modeUpper = (modeName || "").toUpperCase();
  const isLandingOrRtl = (modeUpper === "LAND" || modeUpper === "RTL" || (totalWp > 0 && currentWp >= totalWp));
  
  mobileWaypointMarkers.forEach((m) => {
    if (isLandingOrRtl) {
      m.setOpacity(0.3);
    } else {
      m.setOpacity(1.0);
    }
  });
  
  let landingTarget = null;
  wps.forEach(wp => {
    if (wp.command === 21) {
      landingTarget = [wp.lat, wp.lon];
    }
  });
  if (!landingTarget && wps.length > 0) {
    landingTarget = [wps[0].lat, wps[0].lon];
  }
  
  if (isLandingOrRtl && landingTarget) {
    mobileRunningLine.setLatLngs([
      [droneLat, droneLon],
      landingTarget
    ]);
    
    if (!mobileLandingMarker) {
      mobileLandingMarker = L.marker(landingTarget, {
        icon: L.divIcon({
          html: '<div class="glowing-land-marker" style="transform: translate(-50%, -50%);">LANDING ZONE</div>',
          className: 'leaflet-div-icon',
          iconSize: [100, 24],
          iconAnchor: [50, 12]
        })
      }).addTo(mapInstance);
    } else {
      mobileLandingMarker.setLatLng(landingTarget);
    }
  } else {
    mobileRunningLine.setLatLngs([]);
    if (mobileLandingMarker) {
      mapInstance.removeLayer(mobileLandingMarker);
      mobileLandingMarker = null;
    }
  }
}

function updateDroneLocationOnMap(lat, lon, heading) {
  if (!mapInstance || !droneMarker) return;
  const newLatLng = new L.LatLng(lat, lon);
  droneMarker.setLatLng(newLatLng);
  
  // Center map on drone
  mapInstance.setView(newLatLng, MAP_ZOOM);
  
  // Rotate marker div based on heading
  const markerDiv = document.getElementById('drone-marker-div');
  if (markerDiv) {
    markerDiv.style.transform = `rotate(${heading}deg)`;
  }
}

// 3. WEBSOCKET CONTROLLER
function getInitialServerUrl() {
  const savedUrl = (localStorage.getItem('agri_titan_server_url') || "").trim();

  if (isStaticWebHost) {
    localStorage.setItem('agri_titan_server_url', legacyCloudRelay);
    return legacyCloudRelay;
  }

  return savedUrl || inferredDefaultRelay;
}

function normalizeRelayUrl(inputUrl) {
  let wsUrl = (inputUrl || "").trim();

  if (!wsUrl) {
    const protocol = window.location.protocol === 'https:' ? 'wss:' : 'ws:';
    wsUrl = `${protocol}//${window.location.host}/ws`;
  }

  if (wsUrl.startsWith('http://')) wsUrl = 'ws://' + wsUrl.slice(7);
  if (wsUrl.startsWith('https://')) wsUrl = 'wss://' + wsUrl.slice(8);

  if (!wsUrl.startsWith('ws://') && !wsUrl.startsWith('wss://')) {
    const isHttps = window.location.protocol === 'https:';
    wsUrl = (wsUrl.includes('localhost') || wsUrl.includes('127.0.0.1')) && !isHttps ? 'ws://' + wsUrl : 'wss://' + wsUrl;
  }

  return wsUrl.replace(/\/$/, '').endsWith('/ws') ? wsUrl.replace(/\/$/, '') : wsUrl.replace(/\/$/, '') + '/ws';
}

function getRelayStatusUrl(wsUrl) {
  return wsUrl.replace(/^wss:\/\//, 'https://').replace(/^ws:\/\//, 'http://').replace(/\/ws$/, '/status');
}

function connectWebSocket() {
  if (reconnectTimer) {
    clearTimeout(reconnectTimer);
    reconnectTimer = null;
  }

  customServerUrl = getInitialServerUrl();
  if (serverUrlInput) serverUrlInput.value = customServerUrl;

  const wsUrl = normalizeRelayUrl(customServerUrl);
  currentWsUrl = wsUrl;
  fetchRelayStatus();
  startRelayStatusPolling();
  
  console.log(`Connecting to WebSocket: ${wsUrl}`);
  socket = new WebSocket(wsUrl);
  
  socket.onopen = () => {
    console.log('Connected to server!');
    indServer.classList.add('online');
    
    // Register as mobile client
    socket.send(JSON.stringify({
      type: 'register',
      client: 'mobile',
      passcode: passcode
    }));

    fetchRelayStatus();
  };
  
  socket.onmessage = (event) => {
    try {
      const data = JSON.parse(event.data);
      
      if (data.type === 'gcs_status') {
        handleGcsStatus(data.connected);
      }
      else if (data.type === 'telemetry') {
        handleTelemetryUpdate(data);
      }
      else if (data.type === 'missions_list') {
        handleMissionsList(data.missions);
      }
      else if (data.type === 'error') {
        authError.textContent = data.message.toUpperCase();
        localStorage.removeItem('agri_titan_passcode');
        authScreen.classList.add('active');
        appScreen.classList.remove('active');
        socket.close();
      }
    } catch (err) {
      console.error("Error reading socket payload: ", err);
    }
  };
  
  socket.onclose = () => {
    console.log('Socket disconnected. Reconnecting in 3s...');
    indServer.classList.remove('online');
    
    // Update warning overlay for Server Offline state
    const warningText = document.querySelector('#connection-warning p');
    const warningTitle = document.querySelector('#connection-warning h2');
    if (warningTitle) warningTitle.textContent = "RELAY SERVER OFFLINE";
    if (warningText) {
      warningText.innerHTML = "Cannot connect to your Render cloud server.<br><br>1. Verify your Render web service status is green and <b>Live</b>.<br>2. Verify your Server URL in <b>⚙ Server Settings</b> is correct.";
    }
    
    handleGcsStatus(false);
    reconnectTimer = setTimeout(connectWebSocket, 3000);
  };
}

async function fetchRelayStatus() {
  if (!currentWsUrl) return;

  try {
    const response = await fetch(getRelayStatusUrl(currentWsUrl), { cache: 'no-store' });
    if (!response.ok) return;

    const status = await response.json();
    if (typeof status.gcsConnected === 'boolean') {
      handleGcsStatus(status.gcsConnected);
    }
  } catch (err) {
    console.warn('Relay status check failed:', err);
  }
}

function startRelayStatusPolling() {
  stopRelayStatusPolling();
  relayStatusTimer = setInterval(fetchRelayStatus, 2500);
}

function stopRelayStatusPolling() {
  if (relayStatusTimer) {
    clearInterval(relayStatusTimer);
    relayStatusTimer = null;
  }
}

function handleGcsStatus(connected) {
  lastGcsState = connected;
  if (connected) {
    indGcs.classList.remove('offline');
    indGcs.classList.add('online');
    indGcs.innerHTML = '<span class="pill-dot"></span><span>GCS</span>';
    connectionWarning.classList.remove('active');
    const warningTitle = document.querySelector('#connection-warning .overlay-title');
    const warningText = document.querySelector('#connection-warning .overlay-msg');
    if (warningTitle) warningTitle.textContent = "GCS LINK ACTIVE";
    if (warningText) warningText.innerHTML = `Connected through <b>${currentWsUrl || normalizeRelayUrl(customServerUrl)}</b>`;
    enableControlInputs(true);
  } else {
    indGcs.classList.add('offline');
    indGcs.classList.remove('online');
    indGcs.innerHTML = '<span class="pill-dot"></span><span>GCS</span>';
    connectionWarning.classList.add('active');
    
    if (socket && socket.readyState === WebSocket.OPEN) {
      const warningText = document.querySelector('#connection-warning .overlay-msg');
      const warningTitle = document.querySelector('#connection-warning .overlay-title');
      if (warningTitle) warningTitle.textContent = "AWAITING LAPTOP GCS LINK";
      if (warningText) {
        warningText.innerHTML = `Connected to relay server! Waiting for laptop GCS.<br><br>1. Open <b>relay_config.txt</b> in your GCS folder.<br>2. Enter: <b>${currentWsUrl || normalizeRelayUrl(customServerUrl)}</b><br>3. Run Agri-Titan GCS on your laptop.`;
      }
    } else {
      const warningText = document.querySelector('#connection-warning .overlay-msg');
      const warningTitle = document.querySelector('#connection-warning .overlay-title');
      if (warningTitle) warningTitle.textContent = "RELAY SERVER OFFLINE";
      if (warningText) warningText.innerHTML = "Cannot connect to your Render cloud server.<br><br>Check your Server URL in ⚙ settings.";
    }
    
    resetTelemetryDisplay();
    enableControlInputs(false);
  }
}

function enableControlInputs(enabled) {
  elPumpToggle.disabled = !enabled;
  document.getElementById('btn-rtl').disabled = !enabled;
  document.getElementById('btn-land').disabled = !enabled;
  
  if (enabled) {
    slideArm.classList.remove('disabled');
    slideDisarm.classList.remove('disabled');
    if (isArmedGlobal) {
      slideMission.classList.remove('disabled');
    }
  } else {
    slideArm.classList.add('disabled');
    slideMission.classList.add('disabled');
    slideDisarm.classList.add('disabled');
  }
}

function resetTelemetryDisplay() {
  elDroneId.textContent = "#--";
  
  if (elFlightMode) {
    elFlightMode.textContent = "DISCONNECTED";
    elFlightMode.style.color = '';
  }
  if (elMotorStatus) {
    elMotorStatus.textContent = "STANDBY";
    elMotorStatus.style.color = '';
  }

  if (elBattery) elBattery.textContent = "--%";
  if (elVoltage) elVoltage.textContent = "--V";
  if (elBattFill) { elBattFill.style.width = '0%'; elBattFill.className = 'batt-fill'; }

  if (elAlt) elAlt.textContent = "--m";
  if (elMaxAlt) elMaxAlt.textContent = "--m";
  if (elSpeed) elSpeed.textContent = "--m/s";
  if (elHeading) elHeading.textContent = "--°";

  if (elGpsStatus) elGpsStatus.textContent = "GPS --";
  if (elSats) elSats.textContent = "--";
  if (elHdop) elHdop.textContent = "--";

  if (elPumpToggle) elPumpToggle.checked = false;
  if (elPumpSubText) elPumpSubText.textContent = "OFF";
  
  if (elFlightOpsBadge) elFlightOpsBadge.textContent = "STANDBY";
  if (elMapProgressHud) elMapProgressHud.style.display = 'none';
}

// 4. TELEMETRY DISPLAY HANDLERS
function handleTelemetryUpdate(tele) {
  isArmedGlobal = tele.isArmed;

  // Drone ID
  if (elDroneId) elDroneId.textContent = `#${tele.sysId}`;

  // Flight mode chip
  if (elFlightMode) {
    elFlightMode.textContent = tele.modeName;
    if (tele.modeName === 'AUTO') elFlightMode.style.color = '#16a34a';
    else if (tele.modeName === 'RTL' || tele.modeName === 'LAND') elFlightMode.style.color = '#d97706';
    else elFlightMode.style.color = '';
  }

  // Motor/armed chip
  if (elMotorStatus) {
    if (tele.isArmed) {
      elMotorStatus.textContent = "ARMED";
      elMotorStatus.style.color = '#dc2626';
    } else {
      elMotorStatus.textContent = "STANDBY";
      elMotorStatus.style.color = '';
    }
  }

  // Armed → enable mission slider
  if (tele.isArmed) {
    slideMission.classList.remove('disabled');
    if (elFlightOpsBadge) elFlightOpsBadge.textContent = "ARMED";
  } else {
    slideMission.classList.add('disabled');
    if (elFlightOpsBadge) elFlightOpsBadge.textContent = "STANDBY";
  }

  // Battery
  if (elBattery) elBattery.textContent = `${tele.battery}%`;
  if (elVoltage) elVoltage.textContent = `${tele.voltage.toFixed(1)}V`;
  if (elBattFill) {
    elBattFill.style.width = `${Math.min(tele.battery, 100)}%`;
    elBattFill.className = 'batt-fill';
    if (tele.battery < 20) elBattFill.classList.add('low');
    else if (tele.battery < 50) elBattFill.classList.add('mid');
  }

  // Altitude
  if (elAlt) elAlt.textContent = `${tele.alt.toFixed(1)}m`;
  if (elMaxAlt) elMaxAlt.textContent = `${tele.maxAlt.toFixed(1)}m`;

  // Speed & Heading
  if (elSpeed) elSpeed.textContent = `${tele.speed.toFixed(1)}m/s`;
  if (elHeading) elHeading.textContent = `${tele.heading.toFixed(0)}°`;

  // GPS
  if (elGpsStatus) elGpsStatus.textContent = `GPS ${tele.gpsStatus}`;
  if (elSats) elSats.textContent = tele.sats;
  if (elHdop) elHdop.textContent = tele.hdop.toFixed(1);

  // Pump Status
  if (elPumpToggle) {
    if (tele.pump === 0) {
      elPumpToggle.checked = true;
      if (elPumpSubText) { elPumpSubText.textContent = "ON"; elPumpSubText.style.color = "var(--primary)"; }
    } else {
      elPumpToggle.checked = false;
      if (elPumpSubText) { elPumpSubText.textContent = "OFF"; elPumpSubText.style.color = ""; }
    }
  }

  // Mission progress HUD on map
  if (tele.totalWp > 0 && tele.currentWp > 0) {
    const pct = Math.min(Math.round((tele.currentWp / tele.totalWp) * 100), 100);
    if (elMapProgressHud) elMapProgressHud.style.display = 'block';
    if (elMapProgressPct) elMapProgressPct.textContent = `${pct}%`;
    if (elMapProgressBar) elMapProgressBar.style.width = `${pct}%`;
    if (elMapProgressDetail) elMapProgressDetail.textContent = `${tele.currentWp} / ${tele.totalWp} waypoints`;
  }

  // Auto-detect currently running GCS mission if not set but we have activeMissionsList
  if (tele.totalWp > 0 && (!currentActiveMission || currentActiveMission.waypointsCount !== tele.totalWp)) {
    const matched = activeMissionsList.find(m => m.waypointsCount === tele.totalWp);
    if (matched) {
      currentActiveMission = matched;
      drawMissionOnMobileMap(currentActiveMission);
    }
  }

  // Map updates (only if coordinate is valid)
  if (tele.lat !== 0 && tele.lon !== 0) {
    updateDroneLocationOnMap(tele.lat, tele.lon, tele.heading);
    if (currentActiveMission) {
      updateMobileMissionProgress(tele.currentWp, tele.totalWp, tele.modeName, tele.lat, tele.lon);
    }
  }

  // Active Waypoint Upload Lockout
  const uploadOverlay = document.getElementById('upload-overlay');
  const uploadProgressText = document.getElementById('upload-progress-text');
  const uploadBar = document.getElementById('upload-progress-bar-fill');
  if (tele.isUploading) {
    if (uploadOverlay) uploadOverlay.classList.add('active');
    if (uploadProgressText) {
      uploadProgressText.innerHTML = `Uploading waypoints to drone...<br><b>${tele.uploadProgress}% complete</b>`;
    }
    if (uploadBar) uploadBar.style.width = `${tele.uploadProgress}%`;
    enableControlInputs(false);
  } else {
    if (uploadOverlay) uploadOverlay.classList.remove('active');
    if (lastGcsState) enableControlInputs(true);
  }

  // Logs stream
  if (tele.lastMessage && tele.lastMessage !== "Ready") {
    addAutopilotLog(tele.lastMessage);
  }
}

function updateProgressRing(ringId, percent, color) {
  const ring = document.getElementById(ringId);
  if (!ring) return;
  const offset = RING_CIRCUMFERENCE - (percent / 100) * RING_CIRCUMFERENCE;
  ring.style.strokeDashoffset = offset;
  ring.style.stroke = color;
}

function addAutopilotLog(msg) {
  // Check if log is already listed to prevent duplication
  const existing = Array.from(logList.children).map(li => li.textContent);
  const time = new Date().toLocaleTimeString([], { hour: '2-digit', minute: '2-digit', second: '2-digit' });
  const entry = `[${time}] ${msg}`;
  
  // Check if entry text (excluding time) matches last entry to avoid spamming
  const lastEntry = logList.lastElementChild;
  if (lastEntry && lastEntry.textContent.includes(msg)) return;
  
  const li = document.createElement('li');
  li.className = 'log-item';
  if (msg.includes('WARN') || msg.includes('FAIL') || msg.includes('EMERGENCY') || msg.includes('BREACH')) {
    li.className = 'log-item critical';
  }
  li.textContent = entry;
  
  logList.appendChild(li);
  logList.scrollTop = logList.scrollHeight; // Auto scroll to bottom
  
  // Keep logs list limited to 30 items
  while (logList.children.length > 30) {
    logList.removeChild(logList.firstChild);
  }
}

// 5. SAFETY CONTROL SLIDERS (DRAG & DROP LOGIC)
setupSlider('slide-arm-handle', 'slide-arm-container', () => {
  sendCommand('ARM');
  addAutopilotLog("COMMAND SENT: MOTOR ARM");
});

setupSlider('slide-mission-handle', 'slide-mission-container', () => {
  sendCommand('START_MISSION');
  addAutopilotLog("COMMAND SENT: LAUNCH MISSION");
});

setupSlider('slide-disarm-handle', 'slide-disarm-container', () => {
  sendCommand('DISARM');
  addAutopilotLog("CRITICAL: EMERGENCY DISARM PULLED!");
});

function setupSlider(handleId, containerId, onComplete) {
  const handle = document.getElementById(handleId);
  const container = document.getElementById(containerId);
  
  if (!handle || !container) return;
  
  let isDragging = false;
  let startX = 0;
  let maxSlide = 0;
  
  // Mouse & Touch events
  handle.addEventListener('mousedown', dragStart);
  handle.addEventListener('touchstart', dragStart, { passive: true });
  
  document.addEventListener('mousemove', dragMove);
  document.addEventListener('touchmove', dragMove, { passive: false });
  
  document.addEventListener('mouseup', dragEnd);
  document.addEventListener('touchend', dragEnd);
  
  function dragStart(e) {
    if (container.classList.contains('disabled')) return;
    isDragging = true;
    startX = e.type === 'touchstart' ? e.touches[0].clientX : e.clientX;
    maxSlide = container.clientWidth - handle.clientWidth - 4;
    handle.style.transition = 'none';
  }
  
  function dragMove(e) {
    if (!isDragging) return;
    
    // Prevent screen scroll on touch slide
    if (e.type === 'touchmove') e.preventDefault();
    
    const clientX = e.type === 'touchmove' ? e.touches[0].clientX : e.clientX;
    let deltaX = clientX - startX;
    
    if (deltaX < 0) deltaX = 0;
    if (deltaX > maxSlide) deltaX = maxSlide;
    
    handle.style.left = `${deltaX + 2}px`;
    
    // Slide complete threshold (90%)
    if (deltaX >= maxSlide * 0.95) {
      isDragging = false;
      dragReset(true);
    }
  }
  
  function dragEnd() {
    if (!isDragging) return;
    isDragging = false;
    dragReset(false);
  }
  
  function dragReset(completed) {
    handle.style.transition = 'left 0.3s ease-out';
    handle.style.left = '3px';
    if (completed) {
      // Haptic feedback if mobile device supports it
      if (navigator.vibrate) navigator.vibrate([100, 50, 100]);
      onComplete();
    }
  }
}

// 6. HOLD TO ACTION LOGIC (RTL & LAND)
setupHoldButton('btn-rtl', () => {
  sendCommand('RTL');
  addAutopilotLog("COMMAND SENT: RETURN TO HOME (RTL)");
});

setupHoldButton('btn-land', () => {
  sendCommand('LAND');
  addAutopilotLog("COMMAND SENT: IMMEDIATE LAND");
});

function setupHoldButton(btnId, onComplete) {
  const btn = document.getElementById(btnId);
  if (!btn) return;
  
  let holdTimer = null;
  const HOLD_DURATION = 2000; // 2 seconds hold
  
  btn.addEventListener('mousedown', holdStart);
  btn.addEventListener('touchstart', holdStart, { passive: true });
  
  btn.addEventListener('mouseup', holdEnd);
  btn.addEventListener('touchend', holdEnd);
  btn.addEventListener('mouseleave', holdEnd);
  
  function holdStart(e) {
    if (btn.disabled) return;
    btn.classList.add('confirming');
    
    // Setup hold countdown
    let start = Date.now();
    holdTimer = setTimeout(() => {
      btn.classList.remove('confirming');
      if (navigator.vibrate) navigator.vibrate(200);
      onComplete();
      holdTimer = null;
    }, HOLD_DURATION);
  }
  
  function holdEnd() {
    if (holdTimer) {
      clearTimeout(holdTimer);
      holdTimer = null;
      btn.classList.remove('confirming');
    }
  }
}

// 7. PUMP TOGGLE TRIGGER
elPumpToggle.addEventListener('change', () => {
  if (elPumpToggle.checked) {
    sendCommand('PUMP_ON');
    addAutopilotLog("COMMAND SENT: PUMP START");
  } else {
    sendCommand('PUMP_OFF');
    addAutopilotLog("COMMAND SENT: PUMP STOP");
  }
});

// 8. WS UTILITY
function sendCommand(cmdName) {
  if (socket && socket.readyState === WebSocket.OPEN) {
    socket.send(JSON.stringify({
      type: 'command',
      command: cmdName
    }));
  } else {
    addAutopilotLog("ERROR: Connection to remote server lost.");
  }
}

// 9. LIGHT/DARK THEME CONTROLLER
const themeBtn = document.getElementById('theme-btn');
let currentTheme = localStorage.getItem('agri_titan_theme') || 'light';

if (currentTheme === 'light') {
  document.body.classList.add('light-mode');
  if (themeBtn) themeBtn.textContent = '🌙';
} else {
  document.body.classList.remove('light-mode');
  if (themeBtn) themeBtn.textContent = '☀️';
}

if (themeBtn) {
  themeBtn.addEventListener('click', () => {
    const isLight = document.body.classList.toggle('light-mode');
    currentTheme = isLight ? 'light' : 'dark';
    localStorage.setItem('agri_titan_theme', currentTheme);
    themeBtn.textContent = isLight ? '🌙' : '☀️';
  });
}

// 10. LAPTOP MISSIONS QUICK LOAD & PARAMS DIALOG
const elMissionsContainer = document.getElementById('missions-container');
const elMissionsCount = document.getElementById('lbl-missions-count');
const paramsModal = document.getElementById('params-modal');
const modalSpeedInput = document.getElementById('modal-speed');
const modalHeightInput = document.getElementById('modal-height');
const modalCancelBtn = document.getElementById('modal-cancel-btn');
const modalUploadBtn = document.getElementById('modal-upload-btn');

let selectedMissionId = null;

function handleMissionsList(missions) {
  activeMissionsList = missions; // Cache locally
  if (!elMissionsContainer) return;
  elMissionsContainer.innerHTML = '';
  
  if (elMissionsCount) {
    elMissionsCount.textContent = `${missions.length} SAVED`;
  }
  
  if (missions.length === 0) {
    elMissionsContainer.innerHTML = '<p class="empty-text">No saved missions found on laptop.</p>';
    return;
  }
  
  missions.forEach(m => {
    const card = document.createElement('div');
    card.className = 'mission-item-card';
    card.innerHTML = `
      <div class="mission-details">
        <span class="mission-name">${m.name}</span>
        <span class="mission-sub">${m.waypointsCount} WPs | ${Math.round(m.distance)}m total</span>
      </div>
      <button class="btn-load-mission">QUICK LOAD</button>
    `;
    
    // Bind click to open dialog
    card.querySelector('.btn-load-mission').onclick = () => {
      openMissionParams(m.id);
    };
    
    elMissionsContainer.appendChild(card);
  });
}

function openMissionParams(missionId) {
  selectedMissionId = missionId;
  currentActiveMission = activeMissionsList.find(m => m.id === missionId);
  if (currentActiveMission) {
    drawMissionOnMobileMap(currentActiveMission);
  }
  if (paramsModal) {
    paramsModal.classList.add('active');
  }
}

if (modalCancelBtn) {
  modalCancelBtn.onclick = () => {
    if (paramsModal) paramsModal.classList.remove('active');
  };
}

if (modalUploadBtn) {
  modalUploadBtn.onclick = () => {
    const speed = parseFloat(modalSpeedInput.value) || 5.0;
    const height = parseFloat(modalHeightInput.value) || 5.0;
    
    if (socket && socket.readyState === WebSocket.OPEN && selectedMissionId) {
      socket.send(JSON.stringify({
        type: 'load_mission',
        missionId: selectedMissionId,
        speed: speed,
        height: height
      }));
      addAutopilotLog(`REQUESTING LAPTOP UPLOAD: Speed=${speed}m/s Alt=${height}m`);
    }
    
    if (paramsModal) paramsModal.classList.remove('active');
  };
}
