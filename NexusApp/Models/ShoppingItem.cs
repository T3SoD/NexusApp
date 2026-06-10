namespace NexusApp.Models;

public class ShoppingItem
{
    public string ResourceName { get; set; } = "";
    public double Quantity { get; set; }
    public string Unit { get; set; } = "SCU";   // "SCU" for ship resources, "×" for FPS/vehicle
}
