(() => {
  const streams = RemotePlayStore_SteamUI.remoteClientStreams.filter(s => String(s.m_gameid) === "250820");
  if (streams.length === 0) return {alreadyStopped: true};
  if (streams.length !== 1) throw new Error("Ambiguous inbound VR streams");
  const stream = streams[0];
  const hosts = RemotePlayStore_SteamUI.devices.filter(d => d.clientId === stream.m_clientid && d.bCanStreamVR && d.bHasLocalConnection && d.bConnectedViaWifiAP);
  if (hosts.length !== 1) throw new Error("Stream does not match dedicated-dongle host");
  SteamClient.RemotePlay.StopRemoteClientStream(stream.m_unGroupID, stream.m_steamid, stream.m_gameid, stream.m_clientid);
  return {requested: true, action: "stop-direct-vr"};
})()
