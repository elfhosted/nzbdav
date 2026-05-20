using System.Xml.Linq;
using NWebDav.Server;
using NWebDav.Server.Props;
using NzbWebDAV.Utils;

namespace NzbWebDAV.WebDav.Base;

public class BaseStoreItemPropertyManager() : PropertyManager<BaseStoreItem>(DavProperties)
{
    // See BaseStoreCollectionPropertyManager for the full story: XElement
    // parent ownership is mutable, so a shared static XElement gets
    // re-parented across concurrent PROPFIND requests and corrupts the
    // XmlWriter output. Clone per call.
    private static XElement NewDavResourceType()
        => new(WebDavNamespaces.DavNs + "item");

    private static readonly DavProperty<BaseStoreItem>[] DavProperties =
    [
        new DavDisplayName<BaseStoreItem>
        {
            Getter = item => item.Name
        },
        new DavGetContentLength<BaseStoreItem>
        {
            Getter = item => item.FileSize
        },
        new DavGetContentType<BaseStoreItem>
        {
            Getter = item => ContentTypeUtil.GetContentType(item.Name)
        },
        new DavGetLastModified<BaseStoreItem>
        {
            Getter = x => x.CreatedAt
        },
        new Win32FileAttributes<BaseStoreItem>
        {
            Getter = _ => FileAttributes.Normal
        },
        new DavGetResourceType<BaseStoreItem>
        {
            Getter = _ => [NewDavResourceType()]
        },
        new DavIsCollection<BaseStoreItem>
        {
            Getter = _ => "0"
        }
    ];

    public static readonly BaseStoreItemPropertyManager Instance = new();
}