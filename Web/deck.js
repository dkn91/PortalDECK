(function () {
  'use strict';

  function byId(id) { return document.getElementById(id); }

  var actions = document.querySelectorAll('button[data-action]');
  var mic = actions[0];
  var micDetail = byId('mic-detail');
  var micState = byId('mic-state');
  var connection = byId('connection');
  var feedback = byId('feedback');
  var pairing = byId('pairing');
  var manage = byId('manage-pairing');
  var close = byId('close-pairing');
  var field = byId('pair-token');
  var submit = byId('submit-pairing');
  var pairStatus = byId('pair-status');
  var token = '';
  var generation = 0;
  var stateRequest = null;
  var activeAction = null;
  var resultTile = null;
  var resultTimer;
  var online = false;
  var validatingManual = false;
  var nativeBridge = window.PortalNative;
  var nativeMode = !!(nativeBridge && typeof nativeBridge.sendAction === 'function');

  function updateControls() {
    for (var i = 0; i < actions.length; i++) {
      actions[i].disabled = !online || !pairing.hidden;
      if (activeAction) actions[i].setAttribute('aria-disabled', 'true');
      else actions[i].removeAttribute('aria-disabled');
    }
    field.disabled = submit.disabled = nativeMode || validatingManual || !window.fetch;
  }

  function status(label, tone) {
    online = tone === 'online';
    if (connection.textContent !== label) connection.textContent = label;
    connection.setAttribute('data-tone', tone);
    updateControls();
  }

  function announce(message, tone) {
    feedback.textContent = message;
    feedback.setAttribute('data-tone', tone || 'neutral');
  }

  function message(value, fallback) {
    if (typeof value !== 'string' || !value.trim()) return fallback;
    return (token ? value.split(token).join('[redacted]') : value).slice(0, 240);
  }

  function renderMic(data) {
    var labels = { muted: 'Muted', unmuted: 'Unmuted', mixed: 'Mixed', unavailable: 'Unavailable' };
    var state = data && typeof data.state === 'string' ? data.state.toLowerCase() : '';
    if (state === 'live') state = 'unmuted';
    if (!Object.prototype.hasOwnProperty.call(labels, state)) state = 'unknown';
    var count = data && data.count;
    var detail = typeof count === 'number' && count >= 0
      ? count + ' recording endpoint' + (count === 1 ? '' : 's')
      : 'Recording state unavailable';
    var note = message(data && data.message, '');
    mic.setAttribute('data-state', state);
    if (micState.textContent !== (labels[state] || 'Unknown')) {
      micState.textContent = labels[state] || 'Unknown';
    }
    detail += note ? ' · ' + note : '';
    if (micDetail.textContent !== detail) micDetail.textContent = detail;
  }

  function showPairing(note) {
    if (nativeMode) return;
    pairing.hidden = false;
    manage.setAttribute('aria-expanded', 'true');
    pairStatus.textContent = note || '';
    updateControls();
    (field.disabled ? close : field).focus();
  }

  function hidePairing() {
    if (pairing.hidden) return;
    pairing.hidden = true;
    field.value = '';
    pairStatus.textContent = '';
    manage.setAttribute('aria-expanded', 'false');
    updateControls();
    manage.focus();
  }

  function resetResult() {
    window.clearTimeout(resultTimer);
    if (resultTile) resultTile.removeAttribute('data-result');
    resultTile = null;
  }

  function mark(tile, value) {
    resetResult();
    resultTile = tile;
    tile.setAttribute('data-result', value);
    if (value !== 'pending') resultTimer = window.setTimeout(resetResult, 2500);
  }

  function setSession(nextToken, manual) {
    generation++;
    token = nextToken;
    stateRequest = null;
    activeAction = null;
    validatingManual = manual;
    resetResult();
    renderMic(null);
    status(nextToken ? 'Connecting' : 'Offline', nextToken ? 'connecting' : 'offline');
  }

  function rejectToken() {
    setSession('', false);
    announce('Pairing token rejected.', 'failure');
    showPairing('This token was rejected. Check it and try again.');
  }

  function request(path, action, done) {
    var session = generation;
    var settled = false;
    var controller = window.AbortController ? new window.AbortController() : null;
    var options = {
      method: action ? 'POST' : 'GET',
      headers: {},
      mode: 'same-origin',
      redirect: 'error',
      cache: 'no-store',
      credentials: 'omit'
    };
    if (!nativeMode) options.headers.Authorization = 'Bearer ' + token;
    if (action) {
      options.headers['Content-Type'] = 'application/json';
      options.body = JSON.stringify({ action: action });
    }
    if (controller) options.signal = controller.signal;
    var timeout = window.setTimeout(function () {
      if (controller) controller.abort();
      finish(0, null);
    }, 5000);

    function finish(code, data) {
      if (settled) return;
      settled = true;
      window.clearTimeout(timeout);
      if (session === generation) done(code, data);
    }

    window.fetch(path, options).then(function (response) {
      if (settled || session !== generation) return;
      if (response.status === 401) {
        finish(401, null);
      } else {
        response.json().then(function (data) {
          finish(response.status, data);
        }, function () { finish(response.status, null); });
      }
    }, function () { finish(0, null); });

    return function () {
      settled = true;
      window.clearTimeout(timeout);
      if (controller) controller.abort();
    };
  }

  function refreshState() {
    if ((!nativeMode && !token) || stateRequest || activeAction || !window.fetch) return;
    stateRequest = request('/api/state', null, function (code, data) {
      stateRequest = null;
      if (code === 401) {
        if (!nativeMode) return rejectToken();
        status('Offline', 'offline');
        renderMic(null);
        announce('Pairing rejected. Check pairing in the Android app.', 'failure');
        return;
      }
      var manual = validatingManual;
      validatingManual = false;
      if (code >= 200 && code < 300 && data && typeof data.state === 'string') {
        var wasOnline = online;
        status('Online', 'online');
        renderMic(data);
        if (!wasOnline) {
          announce('Connected. Tap an action to control your desktop.');
        }
        if (manual) hidePairing();
        return;
      }
      if (manual) {
        setSession('', false);
        showPairing('Could not verify the token. Check that the desktop is reachable and try again.');
      } else {
        status('Offline', 'offline');
      }
      renderMic(null);
      if (feedback.getAttribute('data-tone') !== 'failure') announce('Desktop unavailable. Waiting to reconnect.');
    });
  }

  function runAction(event) {
    var tile = event.currentTarget;
    if (tile.disabled || (!nativeMode && !token) || !online || activeAction) return;
    var action = tile.getAttribute('data-action');
    var label = tile.querySelector('strong').textContent;
    if (stateRequest) {
      stateRequest();
      stateRequest = null;
    }
    activeAction = tile;
    mark(tile, 'pending');
    announce('Sending ' + label + '…');
    updateControls();
    function complete(code, data) {
      activeAction = null;
      if (code === 401) return rejectToken();
      var reported = data && typeof data.ok === 'boolean';
      var ok = code >= 200 && code < 300 && reported && data.ok;
      if (!reported) {
        status('Offline', 'offline');
        renderMic(null);
      }
      mark(tile, ok ? 'success' : 'failure');
      announce(message(data && data.message, ok ? label + ' completed.'
        : code === 0 ? label + ' result unavailable. Check the desktop before retrying.'
          : label + ' failed.'), ok ? 'success' : 'failure');
      updateControls();
      refreshState();
    }

    if (!nativeMode) {
      request('/api/action', action, complete);
      return;
    }
    // The native interface is synchronous, so let WebView paint pending feedback first.
    function sendNative() {
      var data;
      try { data = JSON.parse(nativeBridge.sendAction(action)); }
      catch (error) { return complete(0, null); }
      complete(200, data);
    }
    if (window.requestAnimationFrame) {
      window.requestAnimationFrame(function () { window.requestAnimationFrame(sendNative); });
    } else {
      window.setTimeout(sendNative, 32);
    }
  }

  function cleanToken(value) {
    value = typeof value === 'string' ? value.trim() : '';
    return /^[0-9a-f]+$/i.test(value) ? value : '';
  }

  byId('pair-form').addEventListener('submit', function (event) {
    event.preventDefault();
    if (submit.disabled) return;
    var provided = cleanToken(field.value);
    field.value = '';
    if (!provided) {
      showPairing('Enter the hexadecimal pairing token from the desktop.');
      return;
    }
    setSession(provided, true);
    pairStatus.textContent = 'Checking token…';
    close.focus();
    announce('Connecting to your desktop…');
    refreshState();
  });
  manage.addEventListener('click', function () {
    showPairing(window.fetch ? '' : 'This browser cannot connect. Update the browser or WebView.');
  });
  close.addEventListener('click', hidePairing);
  pairing.addEventListener('keydown', function (event) {
    if (event.key === 'Escape' || event.keyCode === 27) {
      event.preventDefault();
      hidePairing();
    } else if (event.key === 'Tab' || event.keyCode === 9) {
      var first = field.disabled ? close : field;
      var last = submit.disabled ? close : submit;
      if (event.shiftKey && document.activeElement === first) {
        event.preventDefault();
        last.focus();
      } else if (!event.shiftKey && document.activeElement === last) {
        event.preventDefault();
        first.focus();
      }
    }
  });
  for (var i = 0; i < actions.length; i++) actions[i].addEventListener('click', runAction);
  window.addEventListener('online', refreshState);
  window.setInterval(refreshState, 3000);
  manage.hidden = nativeMode;
  updateControls();
  if (!window.fetch) {
    announce('Update the browser or WebView to connect.', 'failure');
  } else if (nativeMode) {
    status('Connecting', 'connecting');
    announce('Connecting to your desktop…');
    refreshState();
  }

  var clock = byId('clock');
  var clockDate = byId('clock-date');
  function twoDigits(value) { return (value < 10 ? '0' : '') + value; }
  function tick() {
    var now = new Date();
    var time = twoDigits(now.getHours()) + ':' + twoDigits(now.getMinutes());
    if (clock.textContent !== time) {
      clock.textContent = time;
      clock.setAttribute('datetime', time);
      clockDate.textContent = now.toLocaleDateString(undefined, { weekday: 'short', month: 'short', day: 'numeric' });
    }
  }
  tick();
  window.setInterval(tick, 1000);
}());
