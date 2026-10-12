(() => {
  if (!SystemNetworkStore.isWifiEnabled) throw new Error("Frame Wi-Fi must remain enabled");
  const hosts = RemotePlayStore_SteamUI.devices.filter(d => d.bCanStreamVR && d.bHasSteamVRInstalled && d.bHasLocalConnection && d.bConnectedViaWifiAP);
  if (hosts.length !== 1) throw new Error("Expected exactly one dedicated-dongle VR host");
  SteamClient.Apps.StreamGame(250820, hosts[0].clientId, -1);
  return {requested: true, action: "start-direct-vr"};
})()
