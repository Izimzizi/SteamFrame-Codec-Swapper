"""Exercise the exact Python route classifier shipped to the Frame, without SSH."""
import pathlib
import re
import unittest

root = pathlib.Path(__file__).resolve().parents[2]
source = (root / "SteamLinkVRSettings/Frame/BenchmarkReceiverStartup.cs").read_text()
code = re.search(r'internal const string Python="""\n(.*?)\n""";', source, re.S)[1]
scope = {}
exec(compile(code, "BenchmarkWirelessRoute", "exec"), scope)
route = scope["benchmark_wireless_route"]
not_ready = scope["BenchmarkRouteNotReady"]

def status(*kinds, info=True):
    return dict(bHasInfo=info, links=[dict(type=k, bActive=True) for k in kinds])

class RouteTests(unittest.TestCase):
    def test_aggregate_is_not_a_second_transport(self):
        self.assertEqual("Dedicated wireless", route(status(255, 2)))
        self.assertEqual("Local network", route(status(255, 3)))

    def test_inactive_routes_are_ignored(self):
        value = status(2)
        value["links"].append(dict(type=3, bActive=False))
        self.assertEqual("Dedicated wireless", route(value))

    def test_missing_status_is_transient(self):
        with self.assertRaisesRegex(not_ready, "status is not ready"):
            route(status(2, info=False))

    def test_zero_active_transport_is_transient(self):
        with self.assertRaisesRegex(not_ready, "No active wireless transport"):
            route(status(255))

    def test_multiple_transports_remain_invalid(self):
        with self.assertRaisesRegex(RuntimeError, "Multiple active transports: 2,3") as error:
            route(status(255, 2, 3))
        self.assertNotIsInstance(error.exception, not_ready)

    def test_wired_transport_is_not_retried_as_wireless(self):
        with self.assertRaisesRegex(RuntimeError, "transport type 1") as error:
            route(status(1))
        self.assertNotIsInstance(error.exception, not_ready)

if __name__ == "__main__":
    unittest.main()
