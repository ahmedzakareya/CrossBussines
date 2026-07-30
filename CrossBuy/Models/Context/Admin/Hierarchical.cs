namespace CrossBuy.Models.Context.Admin
{
	public class Hierarchical : BaseEntity
	{
		public int H_ID { get; set; } // Primary Key
		public string? H_Name { get; set; } // Arabic Name
		public string? H_NameEn { get; set; } // English Name
		public int? H_Parent { get; set; } // Parent ID (Self-referencing)
		public string? H_Notes { get; set; } // Notes
		public int? H_Type { get; set; } // Type ID (Foreign Key)
		public int? H_ObjectID { get; set; } // Related Object ID
        public int? Sort { get; set; }
        public bool? IsActive { get; set; }
		public int? deputyID { get; set; } //   deputy   النائب

		// Navigation properties
		public virtual Hierarchical? Parent { get; set; } // Parent Hierarchical
		public virtual ICollection<Hierarchical>? Children { get; set; } // Child Hierarchicals
		public virtual HierarchicalType? Type { get; set; } // Relationship with HierarchicalType
	}
}
