namespace LabQueue.Core.Entities;

public class Certification
{
    public Guid Id { get; set; }
    public string Code { get; set; } = null!;
    public string Name { get; set; } = null!;
    public string? Description { get; set; }

    public ICollection<UserCertification> Holders { get; set; } = new List<UserCertification>();
}
