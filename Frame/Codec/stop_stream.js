(() => {
  const streams = RemotePlayStore_SteamUI.remoteClientStreams.filter(s => String(s.m_gameid) === "250820");
  if (streams.length === 0) return {alreadyStopped: true};
  if (streams.length !== 1) throw new Error("Ambiguous inbound VR streams");
  const stream = streams[0];
  // The one VR stream is stopped whichever link carries it: a stream that is not on the dongle must still be stoppable.
  SteamClient.RemotePlay.StopRemoteClientStream(stream.m_unGroupID, stream.m_steamid, stream.m_gameid, stream.m_clientid);
  return {requested: true, action: "stop-direct-vr"};
})()
