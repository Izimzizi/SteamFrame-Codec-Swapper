(() => {
  if (!SystemNetworkStore.isWifiEnabled) throw new Error("Frame Wi-Fi must remain enabled");
  // Any connection works: the Valve dongle alone, ordinary Wi-Fi alone, or both together. A PC reached over the dongle is preferred, then one on the
  // local network, then any PC that can stream VR; the choice is refused only when the best group holds more than one PC.
  const capable = RemotePlayStore_SteamUI.devices.filter(d => d.bCanStreamVR && d.bHasSteamVRInstalled);
  const groups = [
    capable.filter(d => d.bHasLocalConnection && d.bConnectedViaWifiAP),
    capable.filter(d => d.bHasLocalConnection),
    capable
  ];
  const best = groups.find(group => group.length > 0);
  if (!best) throw new Error("No PC with SteamVR is available to the Frame");
  if (best.length !== 1) throw new Error("More than one PC with SteamVR is available to the Frame (" + best.length + ")");
  SteamClient.Apps.StreamGame(250820, best[0].clientId, -1);
  return {requested: true, action: "start-direct-vr"};
})()
