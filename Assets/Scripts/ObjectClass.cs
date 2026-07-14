namespace Assets.Scripts
{
    public enum ObjectClass
    {
        // Simple experiment (COCO classes — индексы совпадают с COCO)
        Bottle     = 39,
        Cup        = 41,
        Spoon      = 44,
        Tv         = 62,
        Keyboard   = 66,
        Mouse      = 64,

        // Chem lab experiment (кастомная модель — индексы 0..3)
        Beaker     = 0,
        DyeBottle  = 1,
        Rack = 2,
        StirRod    = 3,
        TestTube   = 4,
        WaterBottle = 5
    }
}