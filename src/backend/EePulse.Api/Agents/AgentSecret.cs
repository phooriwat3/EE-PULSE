using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace EePulse.Api.Agents;

internal static class AgentSecret
{
    public static (Guid Id, string WireValue, byte[] Digest) Create(string domain)
    {
        var id=Guid.NewGuid(); var secret=RandomNumberGenerator.GetBytes(32);
        try { return (id,$"{id:N}.{Base64Url(secret)}",Digest(domain,id,secret)); }
        finally { CryptographicOperations.ZeroMemory(secret); }
    }
    public static bool TryParseAndDigest(string domain,string wire,out Guid id,out byte[] digest)
    {
        id=Guid.Empty; digest=[]; var parts=wire.Split('.',2);
        if(parts.Length!=2||!Guid.TryParseExact(parts[0],"N",out id)) return false;
        try { var secret=Convert.FromBase64String(parts[1].Replace('-','+').Replace('_','/')+new string('=',(4-parts[1].Length%4)%4));
            try { if(secret.Length!=32)return false; digest=Digest(domain,id,secret); return true; } finally { CryptographicOperations.ZeroMemory(secret); } }
        catch(FormatException){return false;}
    }
    public static bool EqualsDigest(byte[] left,byte[] right)=>left.Length==right.Length&&CryptographicOperations.FixedTimeEquals(left,right);
    private static byte[] Digest(string domain,Guid id,byte[] secret)
    { using var h=IncrementalHash.CreateHash(HashAlgorithmName.SHA256); h.AppendData(Encoding.UTF8.GetBytes(domain)); h.AppendData([0]); h.AppendData(id.ToByteArray()); h.AppendData(secret); return h.GetHashAndReset(); }
    private static string Base64Url(byte[] value)=>Convert.ToBase64String(value).TrimEnd('=').Replace('+','-').Replace('/','_');
}
