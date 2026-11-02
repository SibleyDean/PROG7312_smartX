namespace SmartX.Gateway.Shared.Services;

public interface IDeploymentValidator
{
    bool ValidateDeploymentTree(DeploymentNode root);

    ValidationResult ValidateWithReasons(DeploymentNode? root);
}

public class DeploymentNode
{
    public string NodeName { get; set; } = string.Empty;
    public string ZoneId { get; set; } = string.Empty;
    public List<DeploymentNode> Children { get; set; } = new();
}

public record ValidationIssue(string NodeId, string Reason);

public record ValidationResult(bool IsValid, int NodesChecked, List<ValidationIssue> Issues);

public class DeploymentValidator : IDeploymentValidator
{
    public bool ValidateDeploymentTree(DeploymentNode root)
    {
        if (root == null) return false;

        if (string.IsNullOrWhiteSpace(root.NodeName) || string.IsNullOrWhiteSpace(root.ZoneId))
        {
            return false;
        }

        // A JSON body can send "children": null, which bypasses the property initialiser.
        if (root.Children == null)
        {
            return true;
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

    // Reports every problem instead of stopping at the first. On top of the name and zone checks above, it flags
    // duplicate node names and nodes whose zone differs from their parent's (the root's children may be in any zone).
    public ValidationResult ValidateWithReasons(DeploymentNode? root)
    {
        if (root is null)
        {
            return new ValidationResult(false, 0, [new ValidationIssue("(root)", "No deployment tree was supplied.")]);
        }

        var issues = new List<ValidationIssue>();
        var seenNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var nodesChecked = 0;

        void Visit(DeploymentNode? node, DeploymentNode? parent, int depth)
        {
            if (node is null)
            {
                issues.Add(new ValidationIssue(NodeId(parent), "Contains an empty child entry."));
                return;
            }

            nodesChecked++;
            var id = NodeId(node, parent);

            if (string.IsNullOrWhiteSpace(node.NodeName))
            {
                issues.Add(new ValidationIssue(id, "Node name is missing."));
            }
            else if (!seenNames.Add(node.NodeName.Trim()))
            {
                issues.Add(new ValidationIssue(id, "Appears more than once in the tree."));
            }

            if (string.IsNullOrWhiteSpace(node.ZoneId))
            {
                issues.Add(new ValidationIssue(id, "Zone is missing."));
            }
            else if (depth >= 2 && parent is not null && !string.IsNullOrWhiteSpace(parent.ZoneId)
                     && !string.Equals(node.ZoneId.Trim(), parent.ZoneId.Trim(), StringComparison.OrdinalIgnoreCase))
            {
                issues.Add(new ValidationIssue(id, $"Zone '{node.ZoneId}' does not match its parent's zone '{parent.ZoneId}'."));
            }

            foreach (var child in node.Children ?? [])
            {
                Visit(child, node, depth + 1);
            }
        }

        Visit(root, null, 0);
        return new ValidationResult(issues.Count == 0, nodesChecked, issues);
    }

    private static string NodeId(DeploymentNode? node, DeploymentNode? parent = null)
    {
        if (node is not null && !string.IsNullOrWhiteSpace(node.NodeName))
        {
            return node.NodeName;
        }

        return parent is null || string.IsNullOrWhiteSpace(parent.NodeName) ? "(unnamed node)" : $"(unnamed node under {parent.NodeName})";
    }
}
