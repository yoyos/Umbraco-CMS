﻿using System;
using System.ComponentModel;
using System.Runtime.Serialization;

namespace Umbraco.Web.Models.ContentEditing
{
    [DataContract(Name = "relationType", Namespace = "")]
    public class RelationTypeDisplay : EntityBasic
    {
        /// <summary>
        /// Gets or sets a boolean indicating whether the RelationType is Bidirectional (true) or Parent to Child (false)
        /// </summary>
        [DataMember(Name = "isBidirectional", IsRequired = true)]
        public bool IsBidirectional { get; set; }

        /// <summary>
        /// Gets or sets the Parents object type id
        /// </summary>
        /// <remarks>Corresponds to the NodeObjectType in the umbracoNode table</remarks>
        [DataMember(Name = "parentObjectType", IsRequired = true)]
        public Guid ParentObjectType { get; set; }

        /// <summary>
        /// Gets or sets the Parent's object type name.
        /// </summary>
        [DataMember(Name = "parentObjectTypeName")]
        [ReadOnly(true)]
        public string ParentObjectTypeName { get; set; }

        /// <summary>
        /// Gets or sets the Childs object type id
        /// </summary>
        /// <remarks>Corresponds to the NodeObjectType in the umbracoNode table</remarks>
        [DataMember(Name = "childObjectType", IsRequired = true)]
        public Guid ChildObjectType { get; set; }

        /// <summary>
        /// Gets or sets the Child's object type name.
        /// </summary>
        [DataMember(Name = "childObjectTypeName")]
        [ReadOnly(true)]
        public string ChildObjectTypeName { get; set; }
    }
}
