using UPnP.Rx.Model;
using UPnP.Rx.Parsing;
using Xunit;

namespace UPnP.Rx.Tests;

public class DescriptionParserTests
{
    private static string Fixture(string name) =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", name));

    private static DeviceDescription ParseFixture(string name, string location)
    {
        var result = DescriptionParser.ParseDeviceDescription(Fixture(name), new Uri(location));

        Assert.True(result.IsSuccess, result.Error);
        return result.Value;
    }

    // ---- Real capture: Linksys WAG200G (IGD:1, UDA 1.0-era URLBase, 3-level device tree) ----

    [Fact]
    public void Linksys_ParsesRootDeviceIdentity()
    {
        var device = ParseFixture("linksys_WAG200G_desc.xml", "http://192.168.1.1:80/rootDesc.xml");

        Assert.Equal("urn:schemas-upnp-org:device:InternetGatewayDevice:1", device.DeviceType);
        Assert.Equal("LINKSYS WAG200G Gateway", device.FriendlyName);
        Assert.Equal("uuid:8ca2eb37-1dd2-11b2-86f1-001a709b5aa8", device.Udn);
        Assert.Equal("WAG200G", device.Upc);
        Assert.Equal(new SpecVersion { Major = 1, Minor = 0 }, device.SpecVersion);
        Assert.Equal(new Uri("http://192.168.1.1/index.htm"), device.PresentationUrl);
    }

    [Fact]
    public void Linksys_UrlBaseWinsOverLocation()
    {
        // LOCATION is port 80, but the document carries URLBase on port 49152 —
        // a device that ships URLBase expects it honored (leniency policy).
        var device = ParseFixture("linksys_WAG200G_desc.xml", "http://192.168.1.1:80/rootDesc.xml");

        Assert.Equal(new Uri("http://192.168.1.1:49152"), device.BaseUrl);

        var l3Forwarding = Assert.Single(device.Services);
        Assert.Equal(new Uri("http://192.168.1.1:49152/upnp/control/L3Forwarding1"), l3Forwarding.ControlUrl);
        Assert.Equal(new Uri("http://192.168.1.1:49152/l3frwd.xml"), l3Forwarding.ScpdUrl);
    }

    [Fact]
    public void Linksys_ParsesEmbeddedDeviceTree()
    {
        var device = ParseFixture("linksys_WAG200G_desc.xml", "http://192.168.1.1:80/rootDesc.xml");

        // Root → { WANDevice → WANConnectionDevice, LANDevice }
        Assert.Equal(2, device.EmbeddedDevices.Count);
        Assert.Equal(4, device.SelfAndDescendants().Count());

        var wanConnection = Assert.Single(device.EmbeddedDevices[0].EmbeddedDevices);
        Assert.Equal("urn:schemas-upnp-org:device:WANConnectionDevice:1", wanConnection.DeviceType);
        Assert.Equal(2, wanConnection.Services.Count);
        Assert.Contains(wanConnection.Services,
            s => s.ServiceType == "urn:schemas-upnp-org:service:WANPPPConnection:1");
    }

    [Fact]
    public void Linksys_UdnWithEmbeddedLineBreakIsNormalized()
    {
        // The real capture contains a line break inside the LANDevice UDN —
        // token values strip all whitespace.
        var device = ParseFixture("linksys_WAG200G_desc.xml", "http://192.168.1.1:80/rootDesc.xml");

        var lanDevice = device.EmbeddedDevices[1];
        Assert.Equal("urn:schemas-upnp-org:device:LANDevice:1", lanDevice.DeviceType);
        Assert.Equal("uuid:8ca2eb36-1dd2-11b2-86f0-001a709b5aa8", lanDevice.Udn);
    }

    // ---- Real capture: Orange Livebox (IGD:2, vendor-namespace elements, icons, empty UPC) ----

    [Fact]
    public void Livebox_ParsesDespiteVendorNamespaceElements()
    {
        var device = ParseFixture("new_LiveBox_desc.xml", "http://192.168.1.1:49152/description.xml");

        Assert.Equal("urn:schemas-upnp-org:device:InternetGatewayDevice:2", device.DeviceType);
        Assert.Equal("Orange Livebox", device.FriendlyName);
        Assert.Equal("Sagemcom", device.Manufacturer);
        Assert.Null(device.Upc);                       // <UPC></UPC> — empty element is unset, not ""
        Assert.Equal(new Uri("http://192.168.1.1"), device.PresentationUrl);
    }

    [Fact]
    public void Livebox_NoUrlBase_ResolvesAgainstLocation()
    {
        var device = ParseFixture("new_LiveBox_desc.xml", "http://192.168.1.1:49152/description.xml");

        Assert.Equal(new Uri("http://192.168.1.1:49152/description.xml"), device.BaseUrl);

        var icon = Assert.Single(device.Icons);
        Assert.Equal("image/png", icon.MimeType);
        Assert.Equal(16, icon.Width);
        Assert.Equal(new Uri("http://192.168.1.1:49152/87895a19/ligd.png"), icon.Url);
    }

    [Fact]
    public void Livebox_FindsServiceAcrossTheDeviceTree()
    {
        var device = ParseFixture("new_LiveBox_desc.xml", "http://192.168.1.1:49152/description.xml");

        var allServices = device.SelfAndDescendants().SelectMany(d => d.Services).ToList();

        Assert.Equal(3, allServices.Count);
        Assert.Contains(allServices,
            s => s.ServiceType == "urn:schemas-upnp-org:service:WANPPPConnection:2");
    }

    // ---- Malformed real-world patterns ----

    [Fact]
    public void UnescapedAmpersands_AreRepairedAndParsed()
    {
        // Defect modeled on real devices (receivers, NAS) emitting bare '&' in
        // friendlyName/manufacturer, which breaks strict XML parsers.
        var device = ParseFixture("malformed_amp_desc.xml", "http://10.0.0.9:8080/desc.xml");

        Assert.Equal("Living Room AV & Media", device.FriendlyName);
        Assert.Equal("D&M Holdings", device.Manufacturer);

        var service = Assert.Single(device.Services);
        Assert.Equal(new Uri("http://10.0.0.9:8080/ContentDirectory/control"), service.ControlUrl);
    }

    [Fact]
    public void MissingNamespace_ParsesViaLocalNames()
    {
        var device = ParseFixture("no_namespace_desc.xml", "http://10.0.0.5:8080/dev/desc.xml");

        Assert.Equal("No Namespace Cam", device.FriendlyName);

        // "../ctl/Control" resolves relative to the document's directory.
        var service = Assert.Single(device.Services);
        Assert.Equal(new Uri("http://10.0.0.5:8080/ctl/Control"), service.ControlUrl);
        Assert.Equal(new Uri("http://10.0.0.5:8080/dev/dummy.xml"), service.ScpdUrl);
    }

    // ---- UDA 2.0 configId ----

    [Fact]
    public void ConfigId_IsReadFromTheRootAttribute()
    {
        const string xml = """
            <?xml version="1.0"?>
            <root xmlns="urn:schemas-upnp-org:device-1-0" configId="1337">
              <specVersion><major>2</major><minor>0</minor></specVersion>
              <device>
                <deviceType>urn:schemas-upnp-org:device:Basic:1</deviceType>
                <UDN>uuid:1</UDN>
              </device>
            </root>
            """;

        var result = DescriptionParser.ParseDeviceDescription(xml, new Uri("http://10.0.0.1/d.xml"));

        Assert.True(result.IsSuccess, result.Error);
        Assert.Equal(1337, result.Value.ConfigId);
        Assert.Equal(new SpecVersion { Major = 2, Minor = 0 }, result.Value.SpecVersion);
    }

    // ---- Failure cases: only when the document identifies nothing ----

    [Fact]
    public void NotXml_Fails()
    {
        var result = DescriptionParser.ParseDeviceDescription("hello, world", new Uri("http://10.0.0.1/d.xml"));

        Assert.False(result.IsSuccess);
        Assert.Contains("not well-formed", result.Error);
    }

    [Fact]
    public void NoDeviceElement_Fails()
    {
        var result = DescriptionParser.ParseDeviceDescription(
            "<root xmlns=\"urn:schemas-upnp-org:device-1-0\"/>", new Uri("http://10.0.0.1/d.xml"));

        Assert.False(result.IsSuccess);
        Assert.Contains("no device element", result.Error);
    }

    [Fact]
    public void RelativeLocation_IsACallerContractViolation()
    {
        Assert.Throws<ArgumentException>(() =>
            DescriptionParser.ParseDeviceDescription("<root/>", new Uri("desc.xml", UriKind.Relative)));
    }
}
