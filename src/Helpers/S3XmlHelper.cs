using System.Xml.Linq;

namespace Stratify.S3.Helpers;

public static class S3XmlHelper
{
    private const string S3Namespace = "http://s3.amazonaws.com/doc/2006-03-01/";
    
    public static string CreateListBucketsResponse(List<(string name, DateTime creationDate)> buckets)
    {
        var root = new XElement(XName.Get("ListAllMyBucketsResult", S3Namespace),
            new XElement("Owner",
                new XElement("ID", "s3proxy"),
                new XElement("DisplayName", "S3 Proxy")
            ),
            new XElement("Buckets",
                buckets.Select(b =>
                    new XElement("Bucket",
                        new XElement("Name", b.name),
                        new XElement("CreationDate", b.creationDate.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'"))
                    )
                )
            )
        );

        return FormatXml(root);
    }

    public static string CreateListObjectsResponse(
        string bucketName,
        string prefix,
        string marker,
        int maxKeys,
        bool isTruncated,
        List<(string key, long size, DateTime lastModified, string etag)> objects)
    {
        var root = new XElement(XName.Get("ListBucketResult", S3Namespace),
            new XElement("Name", bucketName),
            new XElement("Prefix", prefix),
            new XElement("Marker", marker),
            new XElement("MaxKeys", maxKeys),
            new XElement("IsTruncated", isTruncated.ToString().ToLower()),
            objects.Select(obj =>
                new XElement("Contents",
                    new XElement("Key", obj.key),
                    new XElement("LastModified", obj.lastModified.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'")),
                    new XElement("ETag", $"\"{obj.etag}\""),
                    new XElement("Size", obj.size),
                    new XElement("StorageClass", "STANDARD")
                )
            )
        );

        return FormatXml(root);
    }

    public static string CreateErrorResponse(string code, string message, string resource)
    {
        var root = new XElement("Error",
            new XElement("Code", code),
            new XElement("Message", message),
            new XElement("Resource", resource),
            new XElement("RequestId", Guid.NewGuid().ToString())
        );

        return FormatXml(root);
    }

    private static string FormatXml(XElement element)
    {
        var declaration = new XDeclaration("1.0", "UTF-8", null);
        var document = new XDocument(declaration, element);
        
        using var writer = new StringWriter();
        document.Save(writer);
        return writer.ToString();
    }
}