namespace DOPipeline.Entities
{
    public class Entity
    {
        public Guid Id { get; } = Guid.NewGuid();

        public override bool Equals(object obj)
        {
            return obj is Entity entity && Id.Equals(entity.Id);
        }

        public override int GetHashCode()
        {
            return Id.GetHashCode();
        }
    }
}
