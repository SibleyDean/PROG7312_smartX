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

        // Base case: A leaf node (a specific sensor) is validated.
        if (root.Children.Count == 0)
        {
            // A leaf is valid if it has a name and zone ID.
            return !string.IsNullOrWhiteSpace(root.NodeName) && !string.IsNullOrWhiteSpace(root.ZoneId);
        }

        // Recursive step: A parent node is valid only if all its children are valid.
        foreach (var child in root.Children)
        {
            if (!ValidateDeploymentTree(child))
            {
                return false; // Short-circuit on first invalid child.
            }
        }

        return true;
    }
}