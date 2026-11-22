using System.Net;
using System.Text;
using CrossBuy.BL.Hospital;
using Xunit;

namespace CrossBuy.Tests
{
	// ==========================================================================================
	// HOSPITAL H227 — the MLLP listener. What must never happen: a message cut at a chunk boundary filed as two, noise
	// before a frame filed as a message, an endless stream held in memory, a port opened without its interface and key,
	// or a client outside the allowed list heard.
	// ==========================================================================================
	public class HospitalMllpTests
	{
		private static List<byte> Bytes(string s) => Encoding.UTF8.GetBytes(s).ToList();

		[Fact]
		public void Frames_are_taken_whole_however_the_stream_is_cut()
		{
			var buffer = new List<byte>(); var got = new List<string>();
			var wire = Encoding.UTF8.GetBytes("noise\vMSH|^~\\&|A\rOBX|1\x1c\r\vMSH|^~\\&|B\x1c\r\vMSH|partial");
			foreach (var chunk in wire.Chunk(5)) { buffer.AddRange(chunk); Assert.True(HospitalMllp.TryTake(buffer, got)); }
			Assert.Equal(new[] { "MSH|^~\\&|A\rOBX|1", "MSH|^~\\&|B" }, got);
			Assert.Equal("\vMSH|partial", Encoding.UTF8.GetString(buffer.ToArray()));   // kept until its end arrives
		}

		[Fact]
		public void A_lone_end_byte_inside_a_message_does_not_end_it_and_a_frame_carries_arabic()
		{
			var buffer = Bytes("\vNTE|1||a\x1cb\x1c\r"); var got = new List<string>();
			Assert.True(HospitalMllp.TryTake(buffer, got));
			Assert.Equal("NTE|1||a\x1cb", Assert.Single(got));
			var framed = HospitalMllp.Frame("NTE|1||نتيجة");
			Assert.Equal((HospitalMllp.Start, HospitalMllp.End, HospitalMllp.Cr), (framed[0], framed[^2], framed[^1]));
			got.Clear(); Assert.True(HospitalMllp.TryTake(framed.ToList(), got));
			Assert.Equal("NTE|1||نتيجة", Assert.Single(got));
		}

		[Fact]
		public void An_unended_frame_over_the_limit_closes_the_connection()
		{
			var buffer = new List<byte> { HospitalMllp.Start }; buffer.AddRange(new byte[HospitalMllp.MaxFrame + 1]);
			Assert.False(HospitalMllp.TryTake(buffer, new List<string>()));
			var noise = new List<byte>(new byte[HospitalMllp.MaxFrame + 10]);   // no start byte at all: dropped, never held
			Assert.True(HospitalMllp.TryTake(noise, new List<string>()));
			Assert.Empty(noise);
		}

		[Fact]
		public void Only_a_listener_with_a_port_an_interface_and_a_key_opens()
		{
			var (none, _) = HospitalMllp.Read(new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build());
			Assert.Empty(none);
			Assert.Empty(HospitalMllp.Read(null).Ok);
			HospitalMllpListenerOptions L(int port, string iface, string key, string bind = "127.0.0.1") => new() { Port = port, Interface = iface, Key = key, Bind = bind };
			var (ok, skipped) = HospitalMllp.Validate(new[] { L(2575, "LAB-1", "k"), L(2576, "LAB-2", ""), L(2575, "LAB-3", "k"), L(70000, "LAB-4", "k"), L(2577, "LAB-5", "k", "lab-pc") });
			var l = Assert.Single(ok);
			Assert.Equal((2575, "127.0.0.1", "LAB-1"), (l.Port, l.Bind, l.Interface));   // loopback unless told otherwise
			Assert.Equal(4, skipped.Count);
		}

		[Fact]
		public void Only_the_allowed_clients_are_heard_when_a_list_is_given()
		{
			var open = new HospitalMllpListenerOptions();
			Assert.True(HospitalMllp.Allowed(open, IPAddress.Parse("10.0.0.9")));
			var only = new HospitalMllpListenerOptions { AllowedClients = new() { "10.0.0.5" } };
			Assert.True(HospitalMllp.Allowed(only, IPAddress.Parse("10.0.0.5")));
			Assert.True(HospitalMllp.Allowed(only, IPAddress.Parse("10.0.0.5").MapToIPv6()));
			Assert.False(HospitalMllp.Allowed(only, IPAddress.Parse("10.0.0.6")));
			Assert.False(HospitalMllp.Allowed(only, null));
		}

		[Fact]
		public void A_frame_passed_on_over_https_is_unframed_the_same_way() =>
			Assert.Equal("MSH|^~\\&|A", HospitalInterfaceInbound.Unframe("\vMSH|^~\\&|A\x1c\r\n"));
	}
}
