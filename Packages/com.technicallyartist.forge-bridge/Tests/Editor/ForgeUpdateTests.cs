using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEditor.PackageManager;

namespace ForgeBridge.Editor.Tests
{
    public class ForgeUpdateTests
    {
        private static JObject Request(string version = "0.2.2", string protocol = "1.3") =>
            new JObject { ["type"] = "update", ["id"] = "update-1", ["protocol"] = protocol, ["version"] = version };

        [TestCase(PackageSource.Git, true)]
        [TestCase(PackageSource.Local, false)]
        [TestCase(PackageSource.Embedded, false)]
        [TestCase(PackageSource.Registry, false)]
        public void Capability_OnlyOfficialGit(PackageSource source, bool expected)
        {
            Assert.AreEqual(expected, BridgeUpdater.CanSelfUpdate(source, "com.technicallyartist.forge-bridge@" + BridgeUpdater.GitBase + "#0.2.1"));
            Assert.IsFalse(BridgeUpdater.CanSelfUpdate(source, "com.technicallyartist.forge-bridge@" + BridgeUpdater.GitBase.Replace("TechnicallyArtist", "fork") + "#0.2.1"));
            Assert.IsFalse(BridgeUpdater.CanSelfUpdate(source, "com.technicallyartist.forge-bridge@" + BridgeUpdater.GitBase + "evil#0.2.1"));
            var welcome = ForgeProtocol.Welcome(null, "project", "Knight", "6000.0", "0.2.1", expected);
            Assert.AreEqual(expected, (bool?)welcome["plugin"]["selfUpdate"] == true);
        }

        [TestCase("0.2.2", true)]
        [TestCase("0.10.0", true)]
        [TestCase("0.2.1", false)]
        [TestCase("0.2.0", false)]
        [TestCase("0.2.2-preview", false)]
        [TestCase("0.2.2\n", false)]
        [TestCase("00.2.2", false)]
        [TestCase("https://evil.test/package", false)]
        [TestCase("1.2", false)]
        [TestCase("9999999999999999999.2.2", false)]
        public void Version_IsStrictAndNewer(string version, bool valid)
        {
            Assert.AreEqual(valid, BridgeUpdater.Problem(Request(version), 0, "0.2.1", true, false, false, false) == null);
        }

        [Test]
        public void RefusesOldHeaderPayloadAndBusyEditors()
        {
            Assert.That(BridgeUpdater.Problem(Request(protocol: "1.2"), 0, "0.2.1", true, false, false, false), Does.Contain("1.3"));
            Assert.That(BridgeUpdater.Problem(Request(), 1, "0.2.1", true, false, false, false), Does.Contain("payload"));
            Assert.That(BridgeUpdater.Problem(Request(), 0, "0.2.1", false, false, false, false), Does.Contain("local"));
            Assert.That(BridgeUpdater.Problem(Request(), 0, "0.2.1", true, true, false, false), Does.Contain("Play Mode"));
            Assert.That(BridgeUpdater.Problem(Request(), 0, "0.2.1", true, false, true, false), Does.Contain("compiling"));
            Assert.That(BridgeUpdater.Problem(Request(), 0, "0.2.1", true, false, false, true), Does.Contain("importing"));
            Assert.IsTrue(ForgeProtocol.AcceptsVersion("1.2"));
            Assert.IsTrue(ForgeProtocol.AcceptsVersion("1.3"));
            Assert.IsTrue(ForgeProtocol.AcceptsVersion("1.4"));
            Assert.IsFalse(ForgeProtocol.AcceptsVersion("1.5"));
        }

        [Test]
        public async Task SendsStartedBeforeAddAndReportsFailure()
        {
            var replies = new List<JObject>();
            await BridgeUpdater.RunAsync(Request(), null, reply => { replies.Add(reply); return Task.CompletedTask; }, url =>
            {
                Assert.AreEqual("update-started", (string)replies[0]["type"]);
                Assert.AreEqual(BridgeUpdater.GitBase + "#0.2.2", url);
                return Task.FromResult("Git is not installed");
            }, (_, __) => {});
            Assert.AreEqual("update-failed", (string)replies[1]["type"]);
            Assert.AreEqual("update-1", (string)replies[1]["ref"]);
            Assert.AreEqual("Git is not installed", (string)replies[1]["message"]);
        }

        [Test]
        public async Task RefusalNeverCallsPackageManager()
        {
            await BridgeUpdater.RunAsync(Request(), "Exit Play Mode", reply =>
            {
                Assert.AreEqual("update-failed", (string)reply["type"]);
                return Task.CompletedTask;
            }, _ => throw new Exception("must not install"), (_, __) => {});
        }
    }
}
