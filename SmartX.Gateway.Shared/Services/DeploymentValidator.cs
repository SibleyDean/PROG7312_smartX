namespace SmartX.Gateway.Shared.Services;

public interface IDeploymentValidator
{
    bool ValidateDeploymentTree(DeploymentNode root);
}

public class DeploymentNode
{
    public string NodeName { get; set; } = string.Empty;
    public string ZoneId { get; set; } = string.Empty;
    public List<DeploymentNode> Children { get; set; } = new();
}

public class DeploymentValidator : IDeploymentValidator
{
    public bool ValidateDeploymentTree(DeploymentNode root)
    {
        if (root == null) return false;

        if (root.Children.Count == 0)
        {
            return !string.IsNullOrWhiteSpace(root.NodeName) && !string.IsNullOrWhiteSpace(root.ZoneId);
        }

        foreach (var child in root.Children)
        {
            if (!ValidateDeploymentTree(child))
            {
                return false;
            }
        }

        return true;
    }
}