using System.Xml.Linq;
using UPnP.Rx.Parsing;
using Xunit;

namespace UPnP.Rx.Tests;

public class SoapComposerTests
{
    private const string ServiceType = "urn:schemas-upnp-org:service:WANIPConnection:1";

    [Fact]
    public void ComposesEnvelopeWithQualifiedActionAndUnqualifiedArguments()
    {
        var xml = SoapComposer.ComposeActionRequest(ServiceType, "AddPortMapping",
            new Dictionary<string, string>
            {
                ["NewRemoteHost"] = "",
                ["NewExternalPort"] = "8080",
                ["NewProtocol"] = "TCP"
            });

        var document = XDocument.Parse(xml);
        XNamespace s = "http://schemas.xmlsoap.org/soap/envelope/";

        var body = document.Root!.Element(s + "Body");
        Assert.NotNull(body);

        var action = Assert.Single(body.Elements());
        Assert.Equal(XName.Get("AddPortMapping", ServiceType), action.Name);   // qualified by service type
        Assert.Equal("8080", action.Element("NewExternalPort")!.Value);        // unqualified argument
        Assert.Equal("", action.Element("NewRemoteHost")!.Value);              // empty in-arg preserved
    }

    [Fact]
    public void EscapesArgumentValues()
    {
        var xml = SoapComposer.ComposeActionRequest(ServiceType, "AddPortMapping",
            new Dictionary<string, string> { ["NewPortMappingDescription"] = "Tom & Jerry <LAN>" });

        // Strict in what we send: the envelope must re-parse, with the value intact.
        var document = XDocument.Parse(xml);
        Assert.Equal("Tom & Jerry <LAN>", document.Descendants("NewPortMappingDescription").Single().Value);
    }

    [Fact]
    public void SoapActionHeaderIsQuoted() =>
        Assert.Equal(
            "\"urn:schemas-upnp-org:service:WANIPConnection:1#GetExternalIPAddress\"",
            SoapComposer.ComposeSoapActionHeader(ServiceType, "GetExternalIPAddress"));

    [Fact]
    public void EmptyServiceTypeIsACallerContractViolation() =>
        Assert.Throws<ArgumentException>(() => SoapComposer.ComposeActionRequest(" ", "Action"));
}

public class SoapParserTests
{
    // Typical router response, verbatim shape.
    private const string ExternalIpResponse = """
        <?xml version="1.0"?>
        <s:Envelope xmlns:s="http://schemas.xmlsoap.org/soap/envelope/" s:encodingStyle="http://schemas.xmlsoap.org/soap/encoding/">
          <s:Body>
            <u:GetExternalIPAddressResponse xmlns:u="urn:schemas-upnp-org:service:WANIPConnection:1">
              <NewExternalIPAddress>203.0.113.17</NewExternalIPAddress>
            </u:GetExternalIPAddressResponse>
          </s:Body>
        </s:Envelope>
        """;

    // Typical UPnP fault, verbatim shape (718 = ConflictInMappingEntry).
    private const string ConflictFault = """
        <?xml version="1.0"?>
        <s:Envelope xmlns:s="http://schemas.xmlsoap.org/soap/envelope/" s:encodingStyle="http://schemas.xmlsoap.org/soap/encoding/">
          <s:Body>
            <s:Fault>
              <faultcode>s:Client</faultcode>
              <faultstring>UPnPError</faultstring>
              <detail>
                <UPnPError xmlns="urn:schemas-upnp-org:control-1-0">
                  <errorCode>718</errorCode>
                  <errorDescription>ConflictInMappingEntry</errorDescription>
                </UPnPError>
              </detail>
            </s:Fault>
          </s:Body>
        </s:Envelope>
        """;

    [Fact]
    public void ParsesOutArguments()
    {
        var result = SoapParser.ParseActionResponse(ExternalIpResponse, "GetExternalIPAddress");

        Assert.True(result.IsSuccess, result.Error);
        Assert.Equal("203.0.113.17", result.Value["NewExternalIPAddress"]);
        Assert.Equal("203.0.113.17", result.Value["newexternalipaddress"]);   // lenient casing
        Assert.Null(result.Value["NoSuchArgument"]);
    }

    [Fact]
    public void EmptyResponse_YieldsEmptyOutArguments()
    {
        const string xml = """
            <s:Envelope xmlns:s="http://schemas.xmlsoap.org/soap/envelope/">
              <s:Body><u:DeletePortMappingResponse xmlns:u="urn:schemas-upnp-org:service:WANIPConnection:1"/></s:Body>
            </s:Envelope>
            """;

        var result = SoapParser.ParseActionResponse(xml, "DeletePortMapping");

        Assert.True(result.IsSuccess, result.Error);
        Assert.Empty(result.Value.Out);
    }

    [Fact]
    public void WrongResponseElementName_FallsBackLeniently()
    {
        // Devices have been seen answering with a mismatched action name.
        const string xml = """
            <s:Envelope xmlns:s="http://schemas.xmlsoap.org/soap/envelope/">
              <s:Body><GetStatusResponse><NewUptime>12</NewUptime></GetStatusResponse></s:Body>
            </s:Envelope>
            """;

        var result = SoapParser.ParseActionResponse(xml, "GetStatusInfo");

        Assert.True(result.IsSuccess, result.Error);
        Assert.Equal("12", result.Value["NewUptime"]);
    }

    [Fact]
    public void FaultBody_IsRejectedByResponseParser()
    {
        var result = SoapParser.ParseActionResponse(ConflictFault, "AddPortMapping");

        Assert.False(result.IsSuccess);
        Assert.Contains("fault", result.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ParsesUpnpErrorFromFault()
    {
        var result = SoapParser.ParseFault(ConflictFault);

        Assert.True(result.IsSuccess, result.Error);
        Assert.Equal(718, result.Value.Code);
        Assert.Equal("ConflictInMappingEntry", result.Value.Description);
    }

    [Fact]
    public void DuplicateOutArguments_AreLenientlyDeduplicated()
    {
        // Real-world malformation: repeated (case-variant) out-arguments must not
        // make the total parser throw — first occurrence wins.
        const string xml = """
            <s:Envelope xmlns:s="http://schemas.xmlsoap.org/soap/envelope/">
              <s:Body><u:GetXResponse xmlns:u="urn:x">
                <NewValue>first</NewValue><newvalue>second</newvalue>
              </u:GetXResponse></s:Body>
            </s:Envelope>
            """;

        var result = SoapParser.ParseActionResponse(xml, "GetX");

        Assert.True(result.IsSuccess, result.Error);
        Assert.Equal("first", result.Value["NewValue"]);
    }

    [Fact]
    public void SuccessBodyContainingUpnpErrorElement_IsNotAFault()
    {
        // A device echoing error info inside a successful response must not be
        // misclassified: ParseFault requires an actual Fault element.
        const string xml = """
            <s:Envelope xmlns:s="http://schemas.xmlsoap.org/soap/envelope/">
              <s:Body><u:GetLastErrorResponse xmlns:u="urn:x">
                <UPnPError><errorCode>718</errorCode></UPnPError>
              </u:GetLastErrorResponse></s:Body>
            </s:Envelope>
            """;

        Assert.False(SoapParser.ParseFault(xml).IsSuccess);
        Assert.True(SoapParser.ParseActionResponse(xml, "GetLastError").IsSuccess);
    }

    [Fact]
    public void FaultWithoutUpnpError_Fails()
    {
        const string xml = """
            <s:Envelope xmlns:s="http://schemas.xmlsoap.org/soap/envelope/">
              <s:Body><s:Fault><faultcode>s:Client</faultcode></s:Fault></s:Body>
            </s:Envelope>
            """;

        var result = SoapParser.ParseFault(xml);

        Assert.False(result.IsSuccess);
        Assert.Contains("UPnPError", result.Error);
    }

    [Fact]
    public void ComposeAndParse_RoundTrip()
    {
        // Symmetry check: what the composer emits, a device-side parser (or ours,
        // reading back the response shape) can consume.
        var request = SoapComposer.ComposeActionRequest(
            "urn:schemas-upnp-org:service:WANIPConnection:1", "GetExternalIPAddressResponse",
            new Dictionary<string, string> { ["NewExternalIPAddress"] = "198.51.100.7" });

        var result = SoapParser.ParseActionResponse(request, "GetExternalIPAddress");

        Assert.True(result.IsSuccess, result.Error);
        Assert.Equal("198.51.100.7", result.Value["NewExternalIPAddress"]);
    }
}
