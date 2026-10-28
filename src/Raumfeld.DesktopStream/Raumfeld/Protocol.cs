// Raumfeld protocol and DIDL values adapted from node-raumkernel (MIT).
// Copyright (c) 2021 Christian Dürnberger. See licenses/node-raumkernel-MIT.txt.
using System.Xml.Linq;

namespace Raumfeld.DesktopStream.Raumfeld;

public sealed record Room(string Name, string Udn, string? ZoneUdn, string PowerState, IReadOnlyList<RendererIdentity> Renderers);
public sealed record RendererIdentity(string Name, string Udn);
public sealed record Renderer(string Name, string Udn, Uri ControlUrl, string ServiceType);
public sealed record RendererState(string TransportState, string AvTransportUri, string TrackUri)
{
    public bool Owns(Uri uri)
    {
        return string.Equals(AvTransportUri, uri.AbsoluteUri, StringComparison.Ordinal);
    }
}

public static class Protocol
{
    public static IReadOnlyList<Room> ParseRooms(XDocument document)
    {
        return [.. document.Descendants()
            .Where(element => element.Name.LocalName == "room")
            .Select(element => new Room(
                (string?)element.Attribute("name") ?? "",
                (string?)element.Attribute("udn") ?? "",
                (string?)element.Ancestors()
                    .FirstOrDefault(ancestor => ancestor.Name.LocalName == "zone")?.Attribute("udn"),
                (string?)element.Attribute("powerState") ?? "unknown",
                [.. element.Elements()
                    .Where(renderer => renderer.Name.LocalName == "renderer")
                    .Select(renderer => new RendererIdentity(
                        (string?)renderer.Attribute("name") ?? "",
                        (string?)renderer.Attribute("udn") ?? ""))]))];
    }

    public static Room ResolveRoom(IReadOnlyList<Room> rooms, string? selector)
    {
        if (rooms.Count == 0)
        {
            throw new InvalidOperationException("No Raumfeld rooms found, including unassignedRooms.");
        }

        if (string.IsNullOrWhiteSpace(selector) && rooms.Count == 1)
        {
            return rooms[0];
        }

        Room[] matches = [.. rooms.Where(room =>
                new[] { room.Name, room.Udn }
                    .Concat(room.Renderers.SelectMany(renderer => new[] { renderer.Name, renderer.Udn }))
                    .Any(value => string.Equals(value, selector, StringComparison.OrdinalIgnoreCase)))];
        if (matches.Length == 1)
        {
            return matches[0];
        }

        string available = string.Join(", ", rooms.Select(room => $"{room.Name} [{room.Udn}]"));
        throw new InvalidOperationException($"Set roomName to a unique room name or UDN. Available: {available}");
    }

    public static string Metadata(Uri stream)
    {
        XNamespace didl = "urn:schemas-upnp-org:metadata-1-0/DIDL-Lite/";
        XNamespace dublinCore = "http://purl.org/dc/elements/1.1/";
        XNamespace dlna = "urn:schemas-dlna-org:metadata-1-0/";
        XNamespace raumfeld = "urn:schemas-raumfeld-com:meta-data/raumfeld";
        XNamespace upnp = "urn:schemas-upnp-org:metadata-1-0/upnp/";

        var metadata = new XElement(didl + "DIDL-Lite",
            new XAttribute(XNamespace.Xmlns + "dc", dublinCore),
            new XAttribute(XNamespace.Xmlns + "dlna", dlna),
            new XAttribute(XNamespace.Xmlns + "raumfeld", raumfeld),
            new XAttribute(XNamespace.Xmlns + "upnp", upnp),
            new XAttribute("lang", "en"),
            new XElement(didl + "item",
                new XAttribute("parentID", "0"),
                new XAttribute("id", "0/External"),
                new XAttribute("restricted", "1"),
                new XElement(raumfeld + "name", "Station"),
                new XElement(upnp + "class", "object.item.audioItem.audioBroadcast.radio"),
                new XElement(raumfeld + "section", "External"),
                new XElement(raumfeld + "durability", "120"),
                new XElement(dublinCore + "title", ""),
                new XElement(upnp + "signalStrength", "99"),
                new XElement(upnp + "albumArtURI", new XAttribute(dlna + "profileID", "JPEG_TN")),
                new XElement(raumfeld + "ebrowse", ""),
                new XElement(didl + "res",
                    new XAttribute("bitrate", "000"),
                    new XAttribute("protocolInfo", "http-get:*:audio/x-mpegurl:*"),
                    stream.AbsoluteUri)));
        return metadata.ToString(SaveOptions.DisableFormatting);
    }

    public static string Soap(string service, string action, params (string Key, string Value)[] arguments)
    {
        XNamespace soap = "http://schemas.xmlsoap.org/soap/envelope/";
        XNamespace serviceNamespace = service;
        var document = new XDocument(
            new XDeclaration("1.0", "utf-8", null),
            new XElement(soap + "Envelope",
                new XAttribute(XNamespace.Xmlns + "s", soap),
                new XAttribute(soap + "encodingStyle", "http://schemas.xmlsoap.org/soap/encoding/"),
                new XElement(soap + "Body",
                    new XElement(serviceNamespace + action,
                        new XAttribute(XNamespace.Xmlns + "u", serviceNamespace),
                        arguments.Select(argument => new XElement(argument.Key, argument.Value))))));
        return document.ToString(SaveOptions.DisableFormatting);
    }

    public static string Value(XContainer xml, string name)
    {
        return xml.Descendants().FirstOrDefault(e => e.Name.LocalName == name)?.Value ?? "";
    }

    public static Renderer? ParseRenderer(XDocument document, Uri location, string zoneUdn, string serviceName = "AVTransport")
    {
        string baseText = Value(document, "URLBase");
        Uri baseUri = string.IsNullOrEmpty(baseText) ? location : new Uri(baseText);
        foreach (XElement device in document.Descendants().Where(element => element.Name.LocalName == "device"))
        {
            string Field(string name) => device.Elements().FirstOrDefault(e => e.Name.LocalName == name)?.Value ?? "";
            if (!string.Equals(Field("UDN"), zoneUdn, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            XElement? service = device.Elements()
                .FirstOrDefault(element => element.Name.LocalName == "serviceList")?
                .Elements()
                .FirstOrDefault(element => Value(element, "serviceType")
                    .StartsWith($"urn:schemas-upnp-org:service:{serviceName}:", StringComparison.Ordinal));
            if (service != null)
            {
                return new(Field("friendlyName"), zoneUdn, new Uri(baseUri, Value(service, "controlURL")), Value(service, "serviceType"));
            }
        }

        return null;
    }
}
