// STATE & CONFIGURATION
let socket = null;
let lastGcsState = false;
let passcode = localStorage.getItem('agri_titan_passcode') || "";
let customServerUrl = localStorage.getItem('agri_titan_server_url') || "";
let isArmedGlobal = false;
let droneMarker = null;
let mapInstance = null;

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

// Progress Rings Geometry (R = 40 => Circumference = 2 * PI * 40 = 251.2)
const RING_CIRCUMFERENCE = 251.2;

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
  
  L.tileLayer('https://{s}.tile.openstreetmap.org/{z}/{x}/{y}.png', {
    maxZoom: 19
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
function connectWebSocket() {
  let wsUrl = "";
  
  if (customServerUrl) {
    wsUrl = customServerUrl;
    // Format custom URL to ensure correct protocol (ws:// or wss://)
    if (!wsUrl.startsWith('ws://') && !wsUrl.startsWith('wss://')) {
      const isHttps = window.location.protocol === 'https:';
      // If it looks like a secure domain or if the current page is secure, use wss
      if (wsUrl.includes('onrender.com') || wsUrl.includes('railway.app') || isHttps) {
        wsUrl = 'wss://' + wsUrl;
      } else {
        wsUrl = 'ws://' + wsUrl;
      }
    }
    // Ensure path ends with /ws for upgrade handling
    if (!wsUrl.endsWith('/ws')) {
      wsUrl = wsUrl.replace(/\/$/, '') + '/ws';
    }
  } else {
    // Default fallback to self-host
    const protocol = window.location.protocol === 'https:' ? 'wss:' : 'ws:';
    wsUrl = `${protocol}//${window.location.host}/ws`;
  }
  
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
    handleGcsStatus(false);
    setTimeout(connectWebSocket, 3000);
  };
}

function handleGcsStatus(connected) {
  lastGcsState = connected;
  if (connected) {
    indGcs.classList.remove('offline');
    indGcs.classList.add('online');
    indGcs.innerHTML = '<span class="dot"></span> GCS: Online';
    connectionWarning.classList.remove('active');
    
    // Enable controls
    enableControlInputs(true);
  } else {
    indGcs.classList.add('offline');
    indGcs.classList.remove('online');
    indGcs.innerHTML = '<span class="dot"></span> GCS: Offline';
    connectionWarning.classList.add('active');
    
    // Reset Telemetry display
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
  elFlightMode.textContent = "DISCONNECTED";
  elFlightMode.className = "value";
  elMotorStatus.textContent = "STANDBY";
  elMotorStatus.className = "value";
  
  elBattery.textContent = "--%";
  updateProgressRing('ring-battery', 0, '#10b8a6');
  elVoltage.textContent = "--.- V";
  
  elAlt.textContent = "--.-m";
  updateProgressRing('ring-alt', 0, '#8b5cf6');
  elMaxAlt.textContent = "Max: --.-m";
  
  elSpeed.textContent = "--.-m/s";
  updateProgressRing('ring-speed', 0, '#3b82f6');
  elHeading.textContent = "Hdg: --.-°";
  
  elGpsStatus.textContent = "--";
  elSats.textContent = "--";
  elHdop.textContent = "--";
  
  elPumpToggle.checked = false;
  elPumpSubText.textContent = "PUMP STATUS: UNKNOWN";
}

// 4. TELEMETRY DISPLAY HANDLERS
function handleTelemetryUpdate(tele) {
  isArmedGlobal = tele.isArmed;
  
  // Drone metadata
  elDroneId.textContent = `#${tele.sysId}`;
  elFlightMode.textContent = tele.modeName;
  
  // Mode-based class switching
  if (tele.modeName === 'AUTO') {
    elFlightMode.className = "value mode-highlight";
  } else if (tele.modeName === 'RTL' || tele.modeName === 'LAND') {
    elFlightMode.className = "value status-highlight";
  } else {
    elFlightMode.className = "value";
  }
  
  // Armed status
  if (tele.isArmed) {
    elMotorStatus.textContent = "ARMED (ACTIVE)";
    elMotorStatus.className = "value status-highlight";
    slideMission.classList.remove('disabled'); // Allow starting mission if armed
  } else {
    elMotorStatus.textContent = "STANDBY";
    elMotorStatus.className = "value";
    slideMission.classList.add('disabled');
  }
  
  // Battery Gauge
  elBattery.textContent = `${tele.battery}%`;
  elVoltage.textContent = `${tele.voltage.toFixed(1)} V`;
  let battColor = '#10b8a6';
  if (tele.battery < 20) battColor = '#ef4444';
  else if (tele.battery < 50) battColor = '#f59e0b';
  updateProgressRing('ring-battery', tele.battery, battColor);
  
  // Altitude Gauge (Scale ring relative to Max 50m for UI visibility)
  elAlt.textContent = `${tele.alt.toFixed(1)}m`;
  elMaxAlt.textContent = `Max: ${tele.maxAlt.toFixed(1)}m`;
  let altPercent = Math.min((tele.alt / 40.0) * 100, 100); // 40m target altitude scale
  updateProgressRing('ring-alt', altPercent, '#8b5cf6');
  
  // Speed Gauge (Scale ring relative to Max 10m/s)
  elSpeed.textContent = `${tele.speed.toFixed(1)}m/s`;
  elHeading.textContent = `Hdg: ${tele.heading.toFixed(0)}°`;
  let speedPercent = Math.min((tele.speed / 8.0) * 100, 100); // 8m/s max speed scale
  updateProgressRing('ring-speed', speedPercent, '#3b82f6');
  
  // GPS Info
  elGpsStatus.textContent = tele.gpsStatus;
  elSats.textContent = tele.sats;
  elHdop.textContent = tele.hdop.toFixed(1);
  
  // Pump Status
  if (tele.pump === 0) {
    elPumpToggle.checked = true;
    elPumpSubText.textContent = "PUMP STATUS: ACTIVE (SPRAYING)";
    elPumpSubText.style.color = "var(--primary)";
  } else {
    elPumpToggle.checked = false;
    elPumpSubText.textContent = "PUMP STATUS: STOPPED";
    elPumpSubText.style.color = "var(--text-secondary)";
  }
  
  // Map updates (only if coordinate is valid)
  if (tele.lat !== 0 && tele.lon !== 0) {
    updateDroneLocationOnMap(tele.lat, tele.lon, tele.heading);
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
    handle.style.left = '2px';
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
let currentTheme = localStorage.getItem('agri_titan_theme') || 'dark';

if (currentTheme === 'light') {
  document.body.classList.add('light-mode');
  if (themeBtn) themeBtn.textContent = '🌙';
}

if (themeBtn) {
  themeBtn.addEventListener('click', () => {
    const isLight = document.body.classList.toggle('light-mode');
    currentTheme = isLight ? 'light' : 'dark';
    localStorage.setItem('agri_titan_theme', currentTheme);
    themeBtn.textContent = isLight ? '🌙' : '☀️';
  });
}
