using System.Text.Json;
using FluentAssertions;
using JupyterSharp;

namespace JupyterSharp.Tests;

public sealed class JupyterMessageSerializerTests
{
    [Fact]
    public void RoundTripPreservesRoutingMessageAndBuffers()
    {
        var serializer = new JupyterMessageSerializer("secret");
        var created = JupyterMessage.Create(
            "kernel_info_request",
            new JupyterEmptyContent(),
            JupyterJsonContext.Default.JupyterEmptyContent,
            new JupyterSessionIdentity("session-1", "tester"));
        var message = created with
        {
            Header = new JupyterMessageHeader
            {
                MessageId = created.Header.MessageId,
                Username = created.Header.Username,
                Session = created.Header.Session,
                Date = created.Header.Date,
                MessageType = created.Header.MessageType,
                Version = created.Header.Version,
                SubshellId = "subshell-1",
                ExtensionData = new Dictionary<string, JsonElement>
                {
                    ["future_header_field"] = JsonSerializer.SerializeToElement("preserved")
                }
            }
        };
        var wire = new JupyterWireMessage(
            ["client-id"u8.ToArray()],
            message,
            ["buffer"u8.ToArray()]);

        var roundTripped = serializer.Deserialize(serializer.Serialize(wire));

        roundTripped.Message.Header.MessageId.Should().Be(message.Header.MessageId);
        roundTripped.Message.Header.Version.Should().Be("5.5");
        roundTripped.Message.Header.SubshellId.Should().Be("subshell-1");
        roundTripped.Message.Header.ExtensionData?["future_header_field"].GetString().Should().Be("preserved");
        roundTripped.Identities.Single().Should().Equal("client-id"u8.ToArray());
        roundTripped.Buffers.Single().Should().Equal("buffer"u8.ToArray());
    }

    [Fact]
    public void EmptyKeyUsesEmptySignature()
    {
        var serializer = new JupyterMessageSerializer(string.Empty);
        var message = JupyterMessage.Create(
            "kernel_info_request",
            new JupyterEmptyContent(),
            JupyterJsonContext.Default.JupyterEmptyContent,
            JupyterSessionIdentity.Create());

        var frames = serializer.Serialize(JupyterWireMessage.Create(message));

        frames[1].Should().BeEmpty();
        serializer.Deserialize(frames).Message.Header.MessageId.Should().Be(message.Header.MessageId);
    }

    [Fact]
    public void DeserializeRejectsInvalidSignature()
    {
        var serializer = new JupyterMessageSerializer("secret");
        var message = JupyterMessage.Create(
            "kernel_info_request",
            new JupyterEmptyContent(),
            JupyterJsonContext.Default.JupyterEmptyContent,
            JupyterSessionIdentity.Create());
        var frames = serializer.Serialize(JupyterWireMessage.Create(message)).Select(frame => frame.ToArray())
            .ToArray();
        frames[^1] = "{\"tampered\":true}"u8.ToArray();

        serializer.Invoking(s => s.Deserialize(frames))
            .Should().Throw<JupyterProtocolException>().WithMessage("*signature*");
    }

    [Fact]
    public void UnsupportedSignatureSchemeFailsFast()
    {
        FluentActions.Invoking(() => new JupyterMessageSerializer("secret", "hmac-sha512"))
            .Should().Throw<NotSupportedException>().WithMessage("*hmac-sha512*");
    }

    [Fact]
    public void UnsupportedConnectionSignatureSchemeFailsFast()
    {
        var connection = JupyterConnectionInfo.CreateLocalTcp() with { SignatureScheme = "hmac-sha512" };

        var act = connection.ValidateSupported;

        act.Should().Throw<NotSupportedException>().WithMessage("*hmac-sha512*");
    }

    [Fact]
    public void CurveConnectionFailsFast()
    {
        var connection = JupyterConnectionInfo.CreateLocalTcp() with { ServerKey = "curve-server-key" };

        var act = connection.ValidateSupported;

        act.Should().Throw<NotSupportedException>().WithMessage("*CurveZMQ*");
    }

    [Fact]
    public void LanguageServiceDtoRoundTripUsesGeneratedMetadata()
    {
        var reply = new JupyterCompleteReply
        {
            Status = "ok",
            Matches = ["console"],
            CursorStart = 0,
            CursorEnd = 4,
            Metadata = new Dictionary<string, JsonElement>
            {
                ["source"] = JsonSerializer.SerializeToElement("test")
            }
        };

        var json = JsonSerializer.Serialize(reply, JupyterJsonContext.Default.JupyterCompleteReply);
        var roundTripped = JsonSerializer.Deserialize(json, JupyterJsonContext.Default.JupyterCompleteReply);

        roundTripped.Should().NotBeNull();
        roundTripped.Status.Should().Be("ok");
        roundTripped.Matches.Should().Equal("console");
        roundTripped.CursorStart.Should().Be(0);
        roundTripped.CursorEnd.Should().Be(4);
        roundTripped.Metadata["source"].GetString().Should().Be("test");
    }

    [Fact]
    public void KernelInfoMissingNewFieldsUsesCompatibilityDefaults()
    {
        const string json = """
                            {
                              "protocol_version": "5.3",
                              "implementation": "legacy",
                              "implementation_version": "1.0",
                              "language_info": { "name": "test", "version": "1.0" }
                            }
                            """;

        var info = JsonSerializer.Deserialize(json, JupyterJsonContext.Default.JupyterKernelInfo);

        info.Should().NotBeNull();
        info.Status.Should().Be("ok");
        info.Debugger.Should().BeFalse();
        info.SupportedFeatures.Should().BeNull();
    }

    [Fact]
    public void KernelInfoToleratesKernelsThatOmitTheImplementationFields()
    {
        // Ark omits implementation_version entirely, and several kernels omit status although the
        // specification requires it. Rejecting the reply would take GetKernelInfoAsync down for a
        // kernel that otherwise works, so the descriptive fields default instead.
        const string json = """
                            {
                              "protocol_version": "5.3",
                              "language_info": { "name": "ark", "version": "0.1" }
                            }
                            """;

        var info = JsonSerializer.Deserialize(json, JupyterJsonContext.Default.JupyterKernelInfo);

        info.Should().NotBeNull();
        info.ProtocolVersion.Should().Be("5.3");
        info.Implementation.Should().BeEmpty();
        info.ImplementationVersion.Should().BeEmpty();
        info.Status.Should().Be("ok");
        info.LanguageInfo.Name.Should().Be("ark");
    }

    [Fact]
    public void KnownMessageContentMissingAFieldDeserializesToNullRatherThanFailing()
    {
        // Pins the current System.Text.Json behavior, which is weaker than the record signatures
        // imply: a missing constructor argument is bound to default(T), so a non-nullable string
        // ends up holding null and no JsonException is raised. Callers therefore cannot treat
        // "deserialized successfully" as "content is complete".
        //
        // This is deliberately pinned rather than fixed here: enforcing required constructor
        // parameters would change the contract for every protocol DTO at once, and the Jupyter
        // ecosystem does contain kernels that omit fields the specification requires (see the
        // tolerance test above). Decide that globally, not as a side effect of one record.
        var info = JsonSerializer.Deserialize("""{ "language_info": { "name": "ark", "version": "0.1" } }""",
            JupyterJsonContext.Default.JupyterKernelInfo);

        info.Should().NotBeNull();
        info.ProtocolVersion.Should().BeNull();
    }
}
