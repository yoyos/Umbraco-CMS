﻿using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using Umbraco.Core.Configuration;
using Umbraco.Core.Events;
using Umbraco.Core.IO;
using Umbraco.Core.Logging;
using Umbraco.Core.Models;
using Umbraco.Core.Models.Rdbms;
using Umbraco.Core.Persistence;
using Umbraco.Core.Persistence.Querying;
using Umbraco.Core.Persistence.UnitOfWork;
using Umbraco.Core.Strings;

namespace Umbraco.Core.Services
{
    /// <summary>
    /// Represents the Packaging Service, which provides import/export functionality for the Core models of the API
    /// using xml representation. This is primarily used by the Package functionality.
    /// </summary>
    public class PackagingService : IPackagingService
    {
        private readonly IContentService _contentService;
        private readonly IContentTypeService _contentTypeService;
        private readonly IMediaService _mediaService;
        private readonly IDataTypeService _dataTypeService;
        private readonly IFileService _fileService;
        private readonly ILocalizationService _localizationService;
        private readonly RepositoryFactory _repositoryFactory;
        private readonly IDatabaseUnitOfWorkProvider _uowProvider;
        private Dictionary<string, IContentType> _importedContentTypes;


        public PackagingService(IContentService contentService,
            IContentTypeService contentTypeService,
            IMediaService mediaService,
            IDataTypeService dataTypeService,
            IFileService fileService,
            ILocalizationService localizationService,
            RepositoryFactory repositoryFactory,
            IDatabaseUnitOfWorkProvider uowProvider)
        {
            _contentService = contentService;
            _contentTypeService = contentTypeService;
            _mediaService = mediaService;
            _dataTypeService = dataTypeService;
            _fileService = fileService;
            _localizationService = localizationService;
            _repositoryFactory = repositoryFactory;
            _uowProvider = uowProvider;

            _importedContentTypes = new Dictionary<string, IContentType>();
        }
        
        #region Content

        /// <summary>
        /// Exports an <see cref="IContent"/> item to xml as an <see cref="XElement"/>
        /// </summary>
        /// <param name="content">Content to export</param>
        /// <param name="deep">Optional parameter indicating whether to include descendents</param>
        /// <param name="raiseEvents">Optional parameter indicating whether or not to raise events</param>
        /// <returns><see cref="XElement"/> containing the xml representation of the Content object</returns>
        public XElement Export(IContent content, bool deep = false, bool raiseEvents = true)
        {
            if (raiseEvents)
            {
                if (ExportingContent.IsRaisedEventCancelled(new SaveEventArgs<IContent>(content), this))
                    return default(XElement);
            }

            //nodeName should match Casing.SafeAliasWithForcingCheck(content.ContentType.Alias);
            var nodeName = UmbracoConfig.For.UmbracoSettings().Content.UseLegacyXmlSchema ? "node" : content.ContentType.Alias.ToSafeAliasWithForcingCheck();

            var xml = Export(content, nodeName);
            xml.Add(new XAttribute("nodeType", content.ContentType.Id));
            xml.Add(new XAttribute("creatorName", content.GetCreatorProfile().Name));
            xml.Add(new XAttribute("writerName", content.GetWriterProfile().Name));
            xml.Add(new XAttribute("writerID", content.WriterId));
            xml.Add(new XAttribute("template", content.Template == null ? "0" : content.Template.Id.ToString(CultureInfo.InvariantCulture)));
            xml.Add(new XAttribute("nodeTypeAlias", content.ContentType.Alias));

            if (deep)
            {
                var descendants = content.Descendants().ToArray();
                var currentChildren = descendants.Where(x => x.ParentId == content.Id);
                AddChildXml(descendants, currentChildren, xml);
            }

            if(raiseEvents)
                ContentExported.RaiseEvent(new SaveEventArgs<XElement>(xml, false), this);

            return xml;
        }

        /// <summary>
        /// Part of the export of IContent and IMedia and IMember which is shared
        /// </summary>
        /// <param name="contentBase">Base Content or Media to export</param>
        /// <param name="nodeName">Name of the node</param>
        /// <returns><see cref="XElement"/></returns>
        private XElement Export(IContentBase contentBase, string nodeName)
        {
            //NOTE: that one will take care of umbracoUrlName
            var url = contentBase.GetUrlSegment();

            var xml = new XElement(nodeName,
                                   new XAttribute("id", contentBase.Id),
                                   new XAttribute("parentID", contentBase.Level > 1 ? contentBase.ParentId : -1),
                                   new XAttribute("level", contentBase.Level),
                                   new XAttribute("creatorID", contentBase.CreatorId),
                                   new XAttribute("sortOrder", contentBase.SortOrder),
                                   new XAttribute("createDate", contentBase.CreateDate.ToString("s")),
                                   new XAttribute("updateDate", contentBase.UpdateDate.ToString("s")),
                                   new XAttribute("nodeName", contentBase.Name),
                                   new XAttribute("urlName", url),
                                   new XAttribute("path", contentBase.Path),
                                   new XAttribute("isDoc", ""));

            foreach (var property in contentBase.Properties.Where(p => p != null && p.Value != null && p.Value.ToString().IsNullOrWhiteSpace() == false))
            {
                xml.Add(property.ToXml());
            }

            return xml;
        }

        /// <summary>
        /// Used by Content Export to recursively add children
        /// </summary>
        /// <param name="originalDescendants"></param>
        /// <param name="currentChildren"></param>
        /// <param name="currentXml"></param>
        private void AddChildXml(IContent[] originalDescendants, IEnumerable<IContent> currentChildren, XElement currentXml)
        {
            foreach (var child in currentChildren)
            {
                //add the child's xml
                var childXml = Export(child);
                currentXml.Add(childXml);
                //copy local (out of closure)
                var c = child;
                //get this item's children                
                var children = originalDescendants.Where(x => x.ParentId == c.Id);
                //recurse and add it's children to the child xml element
                AddChildXml(originalDescendants, children, childXml);
            }
        }

        /// <summary>
        /// Imports and saves package xml as <see cref="IContent"/>
        /// </summary>
        /// <param name="element">Xml to import</param>
        /// <param name="parentId">Optional parent Id for the content being imported</param>
        /// <param name="userId">Optional Id of the user performing the import</param>
        /// <param name="raiseEvents">Optional parameter indicating whether or not to raise events</param>
        /// <returns>An enumrable list of generated content</returns>
        public IEnumerable<IContent> ImportContent(XElement element, int parentId = -1, int userId = 0, bool raiseEvents = true)
        {
            if (raiseEvents)
            {
                if (ImportingContent.IsRaisedEventCancelled(new SaveEventArgs<XElement>(element), this))
                    return Enumerable.Empty<IContent>();
            }

            var name = element.Name.LocalName;
            if (name.Equals("DocumentSet"))
            {
                //This is a regular deep-structured import
                var roots = from doc in element.Elements()
                            where (string)doc.Attribute("isDoc") == ""
                            select doc;

                var contents = ParseDocumentRootXml(roots, parentId);
                if (contents.Any())
                    _contentService.Save(contents, userId);

                if(raiseEvents)
                    ContentImported.RaiseEvent(new SaveEventArgs<IContent>(contents, false), this);
                return contents;
            }

            var attribute = element.Attribute("isDoc");
            if (attribute != null)
            {
                //This is a single doc import
                var elements = new List<XElement> { element };
                var contents = ParseDocumentRootXml(elements, parentId);
                if (contents.Any())
                    _contentService.Save(contents, userId);

                if(raiseEvents)
                    ContentImported.RaiseEvent(new SaveEventArgs<IContent>(contents, false), this);
                return contents;
            }

            throw new ArgumentException(
                "The passed in XElement is not valid! It does not contain a root element called " +
                "'DocumentSet' (for structured imports) nor is the first element a Document (for single document import).");
        }

        private IEnumerable<IContent> ParseDocumentRootXml(IEnumerable<XElement> roots, int parentId)
        {
            var contents = new List<IContent>();
            foreach (var root in roots)
            {
                bool isLegacySchema = root.Name.LocalName.ToLowerInvariant().Equals("node");
                string contentTypeAlias = isLegacySchema
                                              ? root.Attribute("nodeTypeAlias").Value
                                              : root.Name.LocalName;

                if (_importedContentTypes.ContainsKey(contentTypeAlias) == false)
                {
                    var contentType = FindContentTypeByAlias(contentTypeAlias);
                    _importedContentTypes.Add(contentTypeAlias, contentType);
                }

                var content = CreateContentFromXml(root, _importedContentTypes[contentTypeAlias], null, parentId, isLegacySchema);
                contents.Add(content);

                var children = from child in root.Elements()
                               where (string)child.Attribute("isDoc") == ""
                               select child;
                if (children.Any())
                    contents.AddRange(CreateContentFromXml(children, content, isLegacySchema));
            }
            return contents;
        }

        private IEnumerable<IContent> CreateContentFromXml(IEnumerable<XElement> children, IContent parent, bool isLegacySchema)
        {
            var list = new List<IContent>();
            foreach (var child in children)
            {
                string contentTypeAlias = isLegacySchema
                                              ? child.Attribute("nodeTypeAlias").Value
                                              : child.Name.LocalName;

                if (_importedContentTypes.ContainsKey(contentTypeAlias) == false)
                {
                    var contentType = FindContentTypeByAlias(contentTypeAlias);
                    _importedContentTypes.Add(contentTypeAlias, contentType);
                }

                //Create and add the child to the list
                var content = CreateContentFromXml(child, _importedContentTypes[contentTypeAlias], parent, default(int), isLegacySchema);
                list.Add(content);

                //Recursive call
                XElement child1 = child;
                var grandChildren = from grand in child1.Elements()
                                    where (string)grand.Attribute("isDoc") == ""
                                    select grand;

                if (grandChildren.Any())
                    list.AddRange(CreateContentFromXml(grandChildren, content, isLegacySchema));
            }

            return list;
        }

        private IContent CreateContentFromXml(XElement element, IContentType contentType, IContent parent, int parentId, bool isLegacySchema)
        {
            var id = element.Attribute("id").Value;
            var level = element.Attribute("level").Value;
            var sortOrder = element.Attribute("sortOrder").Value;
            var nodeName = element.Attribute("nodeName").Value;
            var path = element.Attribute("path").Value;
            var template = element.Attribute("template").Value;

            var properties = from property in element.Elements()
                             where property.Attribute("isDoc") == null
                             select property;

            IContent content = parent == null
                                   ? new Content(nodeName, parentId, contentType)
                                   {
                                       Level = int.Parse(level),
                                       SortOrder = int.Parse(sortOrder)
                                   }
                                   : new Content(nodeName, parent, contentType)
                                   {
                                       Level = int.Parse(level),
                                       SortOrder = int.Parse(sortOrder)
                                   };

            foreach (var property in properties)
            {
                string propertyTypeAlias = isLegacySchema ? property.Attribute("alias").Value : property.Name.LocalName;
                if (content.HasProperty(propertyTypeAlias))
                {
                    var propertyValue = property.Value;

                    var propertyType = contentType.PropertyTypes.FirstOrDefault(pt => pt.Alias == propertyTypeAlias);
                    if (propertyType != null && propertyType.PropertyEditorAlias == Constants.PropertyEditors.CheckBoxListAlias)
                    {
                        var database = ApplicationContext.Current.DatabaseContext.Database;
                        var dtos = database.Fetch<DataTypePreValueDto>("WHERE datatypeNo" + "deId = @Id", new { Id = propertyType.DataTypeDefinitionId });

                        var propertyValueList = new List<string>();
                        foreach (var preValue in propertyValue.Split(','))
                        {
                            propertyValueList.Add(dtos.Single(x => x.Value == preValue).Id.ToString(CultureInfo.InvariantCulture));
                        }

                        propertyValue = string.Join(",", propertyValueList.ToArray());
                    }

                    content.SetValue(propertyTypeAlias, propertyValue);
                }
            }

            return content;
        }

        #endregion

        #region ContentTypes

        /// <summary>
        /// Exports an <see cref="IContentType"/> to xml as an <see cref="XElement"/>
        /// </summary>
        /// <param name="contentType">ContentType to export</param>
        /// <param name="raiseEvents">Optional parameter indicating whether or not to raise events</param>
        /// <returns><see cref="XElement"/> containing the xml representation of the ContentType item.</returns>
        public XElement Export(IContentType contentType, bool raiseEvents = true)
        {
            if (raiseEvents)
            {
                if (ExportingContentType.IsRaisedEventCancelled(new SaveEventArgs<IContentType>(contentType), this))
                    return default(XElement);
            }

            var info = new XElement("Info",
                                    new XElement("Name", contentType.Name),
                                    new XElement("Alias", contentType.Alias),
                                    new XElement("Icon", contentType.Icon),
                                    new XElement("Thumbnail", contentType.Thumbnail),
                                    new XElement("Description", contentType.Description),
                                    new XElement("AllowAtRoot", contentType.AllowedAsRoot.ToString()));

            var masterContentType = contentType.CompositionAliases().FirstOrDefault();
            if (masterContentType != null)
                info.Add(new XElement("Master", masterContentType));

            var allowedTemplates = new XElement("AllowedTemplates");
            foreach (var template in contentType.AllowedTemplates)
            {
                allowedTemplates.Add(new XElement("Template", template.Alias));
            }
            info.Add(allowedTemplates);
            if (contentType.DefaultTemplate != null && contentType.DefaultTemplate.Id != 0)
                info.Add(new XElement("DefaultTemplate", contentType.DefaultTemplate.Alias));
            else
                info.Add(new XElement("DefaultTemplate", ""));

            var structure = new XElement("Structure");
            foreach (var allowedType in contentType.AllowedContentTypes)
            {
                structure.Add(new XElement("DocumentType", allowedType.Alias));
            }

            var genericProperties = new XElement("GenericProperties");
            foreach (var propertyType in contentType.PropertyTypes)
            {
                var definition = _dataTypeService.GetDataTypeDefinitionById(propertyType.DataTypeDefinitionId);
                var propertyGroup = contentType.PropertyGroups.FirstOrDefault(x => x.Id == propertyType.PropertyGroupId.Value);
                var genericProperty = new XElement("GenericProperty",
                                                   new XElement("Name", propertyType.Name),
                                                   new XElement("Alias", propertyType.Alias),
                                                   new XElement("Type", propertyType.PropertyEditorAlias),
                                                   new XElement("Definition", definition.Key),
                                                   new XElement("Tab", propertyGroup == null ? "" : propertyGroup.Name),
                                                   new XElement("Mandatory", propertyType.Mandatory.ToString()),
                                                   new XElement("Validation", propertyType.ValidationRegExp),
                                                   new XElement("Description", new XCData(propertyType.Description)));
                genericProperties.Add(genericProperty);
            }

            var tabs = new XElement("Tabs");
            foreach (var propertyGroup in contentType.PropertyGroups)
            {
                var tab = new XElement("Tab",
                                       new XElement("Id", propertyGroup.Id.ToString(CultureInfo.InvariantCulture)),
                                       new XElement("Caption", propertyGroup.Name));
                tabs.Add(tab);
            }

            var xml = new XElement("DocumentType",
                                   info,
                                   structure,
                                   genericProperties,
                                   tabs);

            if (raiseEvents)
                ContentTypeExported.RaiseEvent(new SaveEventArgs<XElement>(xml, false), this);

            return xml;
        }

        /// <summary>
        /// Imports and saves package xml as <see cref="IContentType"/>
        /// </summary>
        /// <param name="element">Xml to import</param>
        /// <param name="userId">Optional id of the User performing the operation. Default is zero (admin).</param>
        /// <param name="raiseEvents">Optional parameter indicating whether or not to raise events</param>
        /// <returns>An enumrable list of generated ContentTypes</returns>
        public IEnumerable<IContentType> ImportContentTypes(XElement element, int userId = 0, bool raiseEvents = true)
        {
            return ImportContentTypes(element, true, userId);
        }

        /// <summary>
        /// Imports and saves package xml as <see cref="IContentType"/>
        /// </summary>
        /// <param name="element">Xml to import</param>
        /// <param name="importStructure">Boolean indicating whether or not to import the </param>
        /// <param name="userId">Optional id of the User performing the operation. Default is zero (admin).</param>
        /// <param name="raiseEvents">Optional parameter indicating whether or not to raise events</param>
        /// <returns>An enumrable list of generated ContentTypes</returns>
        public IEnumerable<IContentType> ImportContentTypes(XElement element, bool importStructure, int userId = 0, bool raiseEvents = true)
        {
            if (raiseEvents)
            {
                if (ImportingContentType.IsRaisedEventCancelled(new SaveEventArgs<XElement>(element), this))
                    return Enumerable.Empty<IContentType>();
            }

            var name = element.Name.LocalName;
            if (name.Equals("DocumentTypes") == false && name.Equals("DocumentType") == false)
            {
                throw new ArgumentException("The passed in XElement is not valid! It does not contain a root element called 'DocumentTypes' for multiple imports or 'DocumentType' for a single import.");
            }

            _importedContentTypes = new Dictionary<string, IContentType>();
            var documentTypes = name.Equals("DocumentTypes")
                                    ? (from doc in element.Elements("DocumentType") select doc).ToList()
                                    : new List<XElement> { element };
            //NOTE it might be an idea to sort the doctype XElements based on dependencies
            //before creating the doc types - should also allow for a better structure/inheritance support.
            foreach (var documentType in documentTypes)
            {
                var alias = documentType.Element("Info").Element("Alias").Value;
                if (_importedContentTypes.ContainsKey(alias) == false)
                {
                    var contentType = _contentTypeService.GetContentType(alias);
                    _importedContentTypes.Add(alias, contentType == null
                                                         ? CreateContentTypeFromXml(documentType)
                                                         : UpdateContentTypeFromXml(documentType, contentType));
                }
            }

            var list = _importedContentTypes.Select(x => x.Value).ToList();
            _contentTypeService.Save(list, userId);

            if (importStructure)
            {
                var updatedContentTypes = new List<IContentType>();
                //Update the structure here - we can't do it untill all DocTypes have been created
                foreach (var documentType in documentTypes)
                {
                    var alias = documentType.Element("Info").Element("Alias").Value;
                    var structureElement = documentType.Element("Structure");
                    //Ensure that we only update ContentTypes which has actual structure-elements
                    if (structureElement == null || structureElement.Elements("DocumentType").Any() == false) continue;

                    var updated = UpdateContentTypesStructure(_importedContentTypes[alias], structureElement);
                    updatedContentTypes.Add(updated);
                }
                //Update ContentTypes with a newly added structure/list of allowed children
                if (updatedContentTypes.Any())
                    _contentTypeService.Save(updatedContentTypes, userId);
            }

            if (raiseEvents)
                ContentTypeImported.RaiseEvent(new SaveEventArgs<IContentType>(list, false), this);

            return list;
        }

        private IContentType CreateContentTypeFromXml(XElement documentType)
        {
            var infoElement = documentType.Element("Info");

            //Name of the master corresponds to the parent
            var masterElement = infoElement.Element("Master");
            IContentType parent = null;
            if (masterElement != null)
            {
                var masterAlias = masterElement.Value;
                parent = _importedContentTypes.ContainsKey(masterAlias)
                             ? _importedContentTypes[masterAlias]
                             : _contentTypeService.GetContentType(masterAlias);
            }

            var contentType = parent == null
                                  ? new ContentType(-1)
                                        {
                                            Alias = infoElement.Element("Alias").Value
                                        }
                                  : new ContentType(parent)
                                        {
                                            Alias = infoElement.Element("Alias").Value
                                        };

            if (parent != null)
                contentType.AddContentType(parent);

            return UpdateContentTypeFromXml(documentType, contentType);
        }

        private IContentType UpdateContentTypeFromXml(XElement documentType, IContentType contentType)
        {
            var infoElement = documentType.Element("Info");
            var defaultTemplateElement = infoElement.Element("DefaultTemplate");

            contentType.Name = infoElement.Element("Name").Value;
            contentType.Icon = infoElement.Element("Icon").Value;
            contentType.Thumbnail = infoElement.Element("Thumbnail").Value;
            contentType.Description = infoElement.Element("Description").Value;
            //NOTE AllowAtRoot is a new property in the package xml so we need to verify it exists before using it.
            if (infoElement.Element("AllowAtRoot") != null)
                contentType.AllowedAsRoot = infoElement.Element("AllowAtRoot").Value.ToLowerInvariant().Equals("true");

            UpdateContentTypesAllowedTemplates(contentType, infoElement.Element("AllowedTemplates"), defaultTemplateElement);
            UpdateContentTypesTabs(contentType, documentType.Element("Tabs"));
            UpdateContentTypesProperties(contentType, documentType.Element("GenericProperties"));

            return contentType;
        }

        private void UpdateContentTypesAllowedTemplates(IContentType contentType,
                                                        XElement allowedTemplatesElement, XElement defaultTemplateElement)
        {
            if (allowedTemplatesElement != null && allowedTemplatesElement.Elements("Template").Any())
            {
                var allowedTemplates = contentType.AllowedTemplates.ToList();
                foreach (var templateElement in allowedTemplatesElement.Elements("Template"))
                {
                    var alias = templateElement.Value;
                    var template = _fileService.GetTemplate(alias.ToSafeAlias());
                    if (template != null)
                    {
                        if (allowedTemplates.Any(x => x.Id == template.Id)) continue;
                        allowedTemplates.Add(template);
                    }
                    else
                    {
                        LogHelper.Warn<PackagingService>(
                            string.Format(
                                "Packager: Error handling allowed templates. Template with alias '{0}' could not be found.",
                                alias));
                    }
                }

                contentType.AllowedTemplates = allowedTemplates;
            }

            if (string.IsNullOrEmpty(defaultTemplateElement.Value) == false)
            {
                var defaultTemplate = _fileService.GetTemplate(defaultTemplateElement.Value.ToSafeAlias());
                if (defaultTemplate != null)
                {
                    contentType.SetDefaultTemplate(defaultTemplate);
                }
                else
                {
                    LogHelper.Warn<PackagingService>(
                        string.Format(
                            "Packager: Error handling default template. Default template with alias '{0}' could not be found.",
                            defaultTemplateElement.Value));
                }
            }
        }

        private void UpdateContentTypesTabs(IContentType contentType, XElement tabElement)
        {
            if (tabElement == null)
                return;

            var tabs = tabElement.Elements("Tab");
            foreach (var tab in tabs)
            {
                var id = tab.Element("Id").Value;//Do we need to use this for tracking?
                var caption = tab.Element("Caption").Value;

                if (contentType.PropertyGroups.Contains(caption) == false)
                {
                    contentType.AddPropertyGroup(caption);

                    int sortOrder;
                    if(tab.Element("SortOrder") != null && 
                        int.TryParse(tab.Element("SortOrder").Value, out sortOrder))
                    {
                        // Override the sort order with the imported value
                        contentType.PropertyGroups[caption].SortOrder = sortOrder;
                    }
                }
            }
        }

        private void UpdateContentTypesProperties(IContentType contentType, XElement genericPropertiesElement)
        {
            var properties = genericPropertiesElement.Elements("GenericProperty");
            foreach (var property in properties)
            {
                var dataTypeDefinitionId = new Guid(property.Element("Definition").Value);//Unique Id for a DataTypeDefinition

                var dataTypeDefinition = _dataTypeService.GetDataTypeDefinitionById(dataTypeDefinitionId);

                //If no DataTypeDefinition with the guid from the xml wasn't found OR the ControlId on the DataTypeDefinition didn't match the DataType Id
                //We look up a DataTypeDefinition that matches

                //we'll check if it is a GUID (legacy id for a property editor)
                var legacyPropertyEditorId = Guid.Empty;
                Guid.TryParse(property.Element("Type").Value, out legacyPropertyEditorId);
                //get the alias as a string for use below
                var propertyEditorAlias = property.Element("Type").Value.Trim();

                //If no DataTypeDefinition with the guid from the xml wasn't found OR the ControlId on the DataTypeDefinition didn't match the DataType Id
                //We look up a DataTypeDefinition that matches

                if (dataTypeDefinition == null)
                {
                    var dataTypeDefinitions = legacyPropertyEditorId != Guid.Empty
                                                  ? _dataTypeService.GetDataTypeDefinitionByControlId(legacyPropertyEditorId)
                                                  : _dataTypeService.GetDataTypeDefinitionByPropertyEditorAlias(propertyEditorAlias);
                    if (dataTypeDefinitions != null && dataTypeDefinitions.Any())
                    {
                        dataTypeDefinition = dataTypeDefinitions.First();
                    }
                }
                else if (legacyPropertyEditorId != Guid.Empty && dataTypeDefinition.ControlId != legacyPropertyEditorId)
                {
                    var dataTypeDefinitions = _dataTypeService.GetDataTypeDefinitionByControlId(legacyPropertyEditorId);
                    if (dataTypeDefinitions != null && dataTypeDefinitions.Any())
                    {
                        dataTypeDefinition = dataTypeDefinitions.First();
                    }
                }
                else if (dataTypeDefinition.PropertyEditorAlias != propertyEditorAlias)
                {
                    var dataTypeDefinitions = _dataTypeService.GetDataTypeDefinitionByPropertyEditorAlias(propertyEditorAlias);
                    if (dataTypeDefinitions != null && dataTypeDefinitions.Any())
                    {
                        dataTypeDefinition = dataTypeDefinitions.First();
                    }
                }
                
                // For backwards compatibility, if no datatype with that ID can be found, we're letting this fail silently.
                // This means that the property will not be created.
                if (dataTypeDefinition == null)
                {
                    LogHelper.Warn<PackagingService>(
                        string.Format("Packager: Error handling creation of PropertyType '{0}'. Could not find DataTypeDefintion with unique id '{1}' nor one referencing the DataType with a property editor alias (or legacy control id) '{2}'. Did the package creator forget to package up custom datatypes? This property will be converted to a label/readonly editor if one exists.",
                                      property.Element("Name").Value,
                                      dataTypeDefinitionId,
                                      property.Element("Type").Value.Trim()));

                    //convert to a label!
                    dataTypeDefinition = _dataTypeService.GetDataTypeDefinitionByPropertyEditorAlias(Constants.PropertyEditors.NoEditAlias).FirstOrDefault();
                    //if for some odd reason this isn't there then ignore
                    if (dataTypeDefinition == null) continue;
                }

                var propertyType = new PropertyType(dataTypeDefinition)
                                       {
                                           Alias = property.Element("Alias").Value,
                                           Name = property.Element("Name").Value,
                                           Description = property.Element("Description").Value,
                                           Mandatory = property.Element("Mandatory").Value.ToLowerInvariant().Equals("true"),
                                           ValidationRegExp = property.Element("Validation").Value
                                       };

                var helpTextElement = property.Element("HelpText");
                if (helpTextElement != null)
                {
                    propertyType.HelpText = helpTextElement.Value;
                }

                var tab = property.Element("Tab").Value;
                if (string.IsNullOrEmpty(tab))
                {
                    contentType.AddPropertyType(propertyType);
                }
                else
                {
                    contentType.AddPropertyType(propertyType, tab);
                }
            }
        }

        private IContentType UpdateContentTypesStructure(IContentType contentType, XElement structureElement)
        {
            var allowedChildren = contentType.AllowedContentTypes.ToList();
            int sortOrder = allowedChildren.Any() ? allowedChildren.Last().SortOrder : 0;
            foreach (var element in structureElement.Elements("DocumentType"))
            {
                var alias = element.Value;
                if (_importedContentTypes.ContainsKey(alias))
                {
                    var allowedChild = _importedContentTypes[alias];
                    if (allowedChild == null || allowedChildren.Any(x => x.Id.IsValueCreated && x.Id.Value == allowedChild.Id)) continue;

                    allowedChildren.Add(new ContentTypeSort(new Lazy<int>(() => allowedChild.Id), sortOrder, allowedChild.Alias));
                    sortOrder++;
                }
                else
                {
                    LogHelper.Warn<PackagingService>(
                    string.Format(
                        "Packager: Error handling DocumentType structure. DocumentType with alias '{0}' could not be found and was not added to the structure for '{1}'.",
                        alias, contentType.Alias));
                }
            }

            contentType.AllowedContentTypes = allowedChildren;
            return contentType;
        }

        private IContentType FindContentTypeByAlias(string contentTypeAlias)
        {
            using (var repository = _repositoryFactory.CreateContentTypeRepository(_uowProvider.GetUnitOfWork()))
            {
                var query = Query<IContentType>.Builder.Where(x => x.Alias == contentTypeAlias);
                var types = repository.GetByQuery(query);

                if (!types.Any())
                    throw new Exception(
                        string.Format("No ContentType matching the passed in Alias: '{0}' was found",
                                      contentTypeAlias));

                var contentType = types.First();

                if (contentType == null)
                    throw new Exception(string.Format("ContentType matching the passed in Alias: '{0}' was null",
                                                      contentTypeAlias));

                return contentType;
            }
        }

        #endregion

        #region DataTypes

        /// <summary>
        /// Export a list of data types
        /// </summary>
        /// <param name="dataTypeDefinitions"></param>
        /// <returns></returns>
        internal XElement Export(IEnumerable<IDataTypeDefinition> dataTypeDefinitions)
        {
            var container = new XElement("DataTypes");
            foreach (var d in dataTypeDefinitions)
            {
                container.Add(Export(d));
            }
            return container;
        }

        internal XElement Export(IDataTypeDefinition dataTypeDefinition)
        {
            var prevalues = new XElement("PreValues");

            var prevalueList = _dataTypeService.GetPreValuesCollectionByDataTypeId(dataTypeDefinition.Id)
                .FormatAsDictionary();

            var sort = 0;
            foreach (var pv in prevalueList)
            {
                var prevalue = new XElement("PreValue");
                prevalue.Add(new XAttribute("Id", pv.Value.Id));
                prevalue.Add(new XAttribute("Value", pv.Value.Value));
                prevalue.Add(new XAttribute("Alias", pv.Key));
                prevalue.Add(new XAttribute("SortOrder", sort));
                prevalues.Add(prevalue);
                sort++;
            }

            var xml = new XElement("DataType", prevalues);
            xml.Add(new XAttribute("Name", dataTypeDefinition.Name));
            //The 'ID' when exporting is actually the property editor alias (in pre v7 it was the IDataType GUID id)
            xml.Add(new XAttribute("Id", dataTypeDefinition.PropertyEditorAlias));
            xml.Add(new XAttribute("Definition", dataTypeDefinition.Key));
            xml.Add(new XAttribute("DatabaseType", dataTypeDefinition.DatabaseType.ToString()));

            return xml;
        }

        /// <summary>
        /// Imports and saves package xml as <see cref="IDataTypeDefinition"/>
        /// </summary>
        /// <param name="element">Xml to import</param>
        /// <param name="userId"></param>
        /// <param name="raiseEvents">Optional parameter indicating whether or not to raise events</param>
        /// <returns>An enumrable list of generated DataTypeDefinitions</returns>
        public IEnumerable<IDataTypeDefinition> ImportDataTypeDefinitions(XElement element, int userId = 0, bool raiseEvents = true)
        {
            if (raiseEvents)
            {
                if (ImportingDataType.IsRaisedEventCancelled(new SaveEventArgs<XElement>(element), this))
                    return Enumerable.Empty<IDataTypeDefinition>();
            }

            var name = element.Name.LocalName;
            if (name.Equals("DataTypes") == false && name.Equals("DataType") == false)
            {
                throw new ArgumentException("The passed in XElement is not valid! It does not contain a root element called 'DataTypes' for multiple imports or 'DataType' for a single import.");
            }

            var dataTypes = new Dictionary<string, IDataTypeDefinition>();
            var dataTypeElements = name.Equals("DataTypes")
                                       ? (from doc in element.Elements("DataType") select doc).ToList()
                                       : new List<XElement> { element.Element("DataType") };

            foreach (var dataTypeElement in dataTypeElements)
            {
                var dataTypeDefinitionName = dataTypeElement.Attribute("Name").Value;

                var legacyPropertyEditorId = Guid.Empty;
                Guid.TryParse(dataTypeElement.Attribute("Id").Value, out legacyPropertyEditorId);

                var dataTypeDefinitionId = new Guid(dataTypeElement.Attribute("Definition").Value);
                var databaseTypeAttribute = dataTypeElement.Attribute("DatabaseType");

                var definition = _dataTypeService.GetDataTypeDefinitionById(dataTypeDefinitionId);
                //If the datatypedefinition doesn't already exist we create a new new according to the one in the package xml
                if (definition == null)
                {
                    var databaseType = databaseTypeAttribute != null
                                           ? databaseTypeAttribute.Value.EnumParse<DataTypeDatabaseType>(true)
                                           : DataTypeDatabaseType.Ntext;

                    //check if the Id was a GUID, that means it is referenced using the legacy property editor GUID id
                    if (legacyPropertyEditorId != Guid.Empty)
                    {
                        var dataTypeDefinition = new DataTypeDefinition(-1, legacyPropertyEditorId)
                            {
                                Key = dataTypeDefinitionId,
                                Name = dataTypeDefinitionName,
                                DatabaseType = databaseType
                            };
                        dataTypes.Add(dataTypeDefinitionName, dataTypeDefinition);
                    }
                    else
                    {
                        //the Id field is actually the string property editor Alias
                        var dataTypeDefinition = new DataTypeDefinition(-1, dataTypeElement.Attribute("Id").Value.Trim())
                        {
                            Key = dataTypeDefinitionId,
                            Name = dataTypeDefinitionName,
                            DatabaseType = databaseType
                        };
                        dataTypes.Add(dataTypeDefinitionName, dataTypeDefinition);
                    }

                }
            }

            var list = dataTypes.Select(x => x.Value).ToList();
            if (list.Any())
            {
                _dataTypeService.Save(list, userId);

                SavePrevaluesFromXml(list, dataTypeElements);
            }

            if (raiseEvents)
                DataTypeImported.RaiseEvent(new SaveEventArgs<IDataTypeDefinition>(list, false), this);

            return list;
        }

        private void SavePrevaluesFromXml(List<IDataTypeDefinition> dataTypes, IEnumerable<XElement> dataTypeElements)
        {
            foreach (var dataTypeElement in dataTypeElements)
            {
                var prevaluesElement = dataTypeElement.Element("PreValues");
                if (prevaluesElement == null) continue;

                var dataTypeDefinitionName = dataTypeElement.Attribute("Name").Value;
                var dataTypeDefinition = dataTypes.First(x => x.Name == dataTypeDefinitionName);

                var valuesWithoutKeys = prevaluesElement.Elements("PreValue")
                                                        .Where(x => ((string) x.Attribute("Alias")).IsNullOrWhiteSpace())
                                                        .Select(x => x.Attribute("Value").Value);

                var valuesWithKeys = prevaluesElement.Elements("PreValue")
                                                     .Where(x => ((string) x.Attribute("Alias")).IsNullOrWhiteSpace() == false)
                                                     .ToDictionary(key => (string) key.Attribute("Alias"), val => new PreValue((string) val.Attribute("Value")));
                
                //save the values with keys
                _dataTypeService.SavePreValues(dataTypeDefinition.Id, valuesWithKeys);

                //save the values without keys (this is legacy)
                _dataTypeService.SavePreValues(dataTypeDefinition.Id, valuesWithoutKeys);
            }
        }

        #endregion

        #region Dictionary Items

        public XElement Export(IEnumerable<IDictionaryItem> dictionaryItem, bool includeChildren = true)
        {
            var xml = new XElement("DictionaryItems");
            foreach (var item in dictionaryItem)
            {
                xml.Add(Export(item, includeChildren));
            }
            return xml;
        }

        public XElement Export(IDictionaryItem dictionaryItem, bool includeChildren)
        {
            var xml = new XElement("DictionaryItem", new XAttribute("Key", dictionaryItem.ItemKey));
            foreach (var translation in dictionaryItem.Translations)
            {
                xml.Add(new XElement("Value",
                    new XAttribute("LanguageId", translation.Language.Id),
                    new XAttribute("LanguageCultureAlias", translation.Language.IsoCode),
                    new XCData(translation.Value)));
            }

            if (includeChildren)
            {
                var children = _localizationService.GetDictionaryItemChildren(dictionaryItem.Key);
                foreach (var child in children)
                {
                    xml.Add(Export(child, true));
                }
            }

            return xml;
        }

        public IEnumerable<IDictionaryItem> ImportDictionaryItems(XElement dictionaryItemElementList)
        {
            var languages = _localizationService.GetAllLanguages().ToList();
            return ImportDictionaryItems(dictionaryItemElementList, languages);
        }

        private IEnumerable<IDictionaryItem> ImportDictionaryItems(XElement dictionaryItemElementList, List<ILanguage> languages)
        {
            var items = new List<IDictionaryItem>();
            foreach (var dictionaryItemElement in dictionaryItemElementList.Elements("DictionaryItem"))
                items.AddRange(ImportDictionaryItem(dictionaryItemElement, languages));
            return items;
        }

        private IEnumerable<IDictionaryItem> ImportDictionaryItem(XElement dictionaryItemElement, List<ILanguage> languages)
        {
            var items = new List<IDictionaryItem>();

            IDictionaryItem dictionaryItem;
            var key = dictionaryItemElement.Attribute("Key").Value;
            if (_localizationService.DictionaryItemExists(key))
                dictionaryItem = GetAndUpdateDictionaryItem(key, dictionaryItemElement, languages);
            else
                dictionaryItem = CreateNewDictionaryItem(key, dictionaryItemElement, languages);
            _localizationService.Save(dictionaryItem);
            items.Add(dictionaryItem);
            items.AddRange(ImportDictionaryItems(dictionaryItemElement, languages));
            return items;
        }

        private IDictionaryItem GetAndUpdateDictionaryItem(string key, XElement dictionaryItemElement, List<ILanguage> languages)
        {
            var dictionaryItem = _localizationService.GetDictionaryItemByKey(key);
            var translations = dictionaryItem.Translations.ToList();
            foreach (var valueElement in dictionaryItemElement.Elements("Value").Where(v => DictionaryValueIsNew(translations, v)))
                AddDictionaryTranslation(translations, valueElement, languages);
            dictionaryItem.Translations = translations;
            return dictionaryItem;
        }

        private static DictionaryItem CreateNewDictionaryItem(string key, XElement dictionaryItemElement, List<ILanguage> languages)
        {
            var dictionaryItem = new DictionaryItem(key);
            var translations = new List<IDictionaryTranslation>();

            foreach (var valueElement in dictionaryItemElement.Elements("Value"))
                AddDictionaryTranslation(translations, valueElement, languages);

            dictionaryItem.Translations = translations;
            return dictionaryItem;
        }

        private static bool DictionaryValueIsNew(IEnumerable<IDictionaryTranslation> translations, XElement valueElement)
        {
            return translations.All(t =>
                String.Compare(t.Language.IsoCode, valueElement.Attribute("LanguageCultureAlias").Value, StringComparison.InvariantCultureIgnoreCase) != 0
                );
        }

        private static void AddDictionaryTranslation(ICollection<IDictionaryTranslation> translations, XElement valueElement, IEnumerable<ILanguage> languages)
        {
            var languageId = valueElement.Attribute("LanguageCultureAlias").Value;
            var language = languages.SingleOrDefault(l => l.IsoCode == languageId);
            if (language == null)
                return;
            var translation = new DictionaryTranslation(language, valueElement.Value);
            translations.Add(translation);
        }

        #endregion

        #region Files
        #endregion

        #region Languages

        public XElement Export(IEnumerable<ILanguage> languages)
        {
            var xml = new XElement("Languages");
            foreach (var language in languages)
            {
                xml.Add(Export(language));
            }
            return xml;
        }

        public XElement Export(ILanguage language)
        {
            var xml = new XElement("Language",
                new XAttribute("Id", language.Id),
                new XAttribute("CultureAlias", language.IsoCode),
                new XAttribute("FriendlyName", language.CultureName));
            return xml;
        }

        public IEnumerable<ILanguage> ImportLanguages(XElement languageElementList)
        {
            var list = new List<ILanguage>();
            foreach (var languageElement in languageElementList.Elements("Language"))
            {
                var isoCode = languageElement.Attribute("CultureAlias").Value;
                var existingLanguage = _localizationService.GetLanguageByIsoCode(isoCode);
                if (existingLanguage == null)
                {
                    var langauge = new Language(isoCode)
                                   {
                                       CultureName = languageElement.Attribute("FriendlyName").Value
                                   };
                    _localizationService.Save(langauge);
                    list.Add(langauge);
                }
            }

            return list;
        }

        #endregion

        #region Macros
        #endregion

        #region Members

        /// <summary>
        /// Exports an <see cref="IMedia"/> item to xml as an <see cref="XElement"/>
        /// </summary>
        /// <param name="member">Member to export</param>
        /// <returns><see cref="XElement"/> containing the xml representation of the Member object</returns>
        internal XElement Export(IMember member)
        {
            //nodeName should match Casing.SafeAliasWithForcingCheck(content.ContentType.Alias);
            var nodeName = UmbracoConfig.For.UmbracoSettings().Content.UseLegacyXmlSchema ? "node" : member.ContentType.Alias.ToSafeAliasWithForcingCheck();

            var xml = Export(member, nodeName);
            xml.Add(new XAttribute("nodeType", member.ContentType.Id));
            xml.Add(new XAttribute("nodeTypeAlias", member.ContentType.Alias));

            xml.Add(new XAttribute("loginName", member.Username));
            xml.Add(new XAttribute("email", member.Email));
            xml.Add(new XAttribute("key", member.Key));

            return xml;
        }

        #endregion

        #region Media

        /// <summary>
        /// Exports an <see cref="IMedia"/> item to xml as an <see cref="XElement"/>
        /// </summary>
        /// <param name="media">Media to export</param>
        /// <param name="deep">Optional parameter indicating whether to include descendents</param>
        /// <param name="raiseEvents">Optional parameter indicating whether or not to raise events</param>
        /// <returns><see cref="XElement"/> containing the xml representation of the Media object</returns>
        public XElement Export(IMedia media, bool deep = false, bool raiseEvents = true)
        {
            if (raiseEvents)
            {
                if (ExportingMedia.IsRaisedEventCancelled(new SaveEventArgs<IMedia>(media), this))
                    return default(XElement);
            }

            //nodeName should match Casing.SafeAliasWithForcingCheck(content.ContentType.Alias);
            var nodeName = UmbracoConfig.For.UmbracoSettings().Content.UseLegacyXmlSchema ? "node" : media.ContentType.Alias.ToSafeAliasWithForcingCheck();

            var xml = Export(media, nodeName);
            xml.Add(new XAttribute("nodeType", media.ContentType.Id));
            xml.Add(new XAttribute("writerName", media.GetCreatorProfile().Name));
            xml.Add(new XAttribute("writerID", media.CreatorId));
            xml.Add(new XAttribute("version", media.Version));
            xml.Add(new XAttribute("template", 0));
            xml.Add(new XAttribute("nodeTypeAlias", media.ContentType.Alias));

            if (deep)
            {
                var descendants = media.Descendants().ToArray();
                var currentChildren = descendants.Where(x => x.ParentId == media.Id);
                AddChildXml(descendants, currentChildren, xml);
            }

            if(raiseEvents)
                MediaExported.RaiseEvent(new SaveEventArgs<XElement>(xml, false), this);

            return xml;
        }

        /// <summary>
        /// Used by Media Export to recursively add children
        /// </summary>
        /// <param name="originalDescendants"></param>
        /// <param name="currentChildren"></param>
        /// <param name="currentXml"></param>
        private void AddChildXml(IMedia[] originalDescendants, IEnumerable<IMedia> currentChildren, XElement currentXml)
        {
            foreach (var child in currentChildren)
            {
                //add the child's xml
                var childXml = Export(child);
                currentXml.Add(childXml);
                //copy local (out of closure)
                var c = child;
                //get this item's children                
                var children = originalDescendants.Where(x => x.ParentId == c.Id);
                //recurse and add it's children to the child xml element
                AddChildXml(originalDescendants, children, childXml);
            }
        }

        #endregion

        #region MediaTypes

        /// <summary>
        /// Exports an <see cref="IMediaType"/> to xml as an <see cref="XElement"/>
        /// </summary>
        /// <param name="mediaType">MediaType to export</param>
        /// <returns><see cref="XElement"/> containing the xml representation of the MediaType item.</returns>
        internal XElement Export(IMediaType mediaType)
        {
            var info = new XElement("Info",
                                    new XElement("Name", mediaType.Name),
                                    new XElement("Alias", mediaType.Alias),
                                    new XElement("Icon", mediaType.Icon),
                                    new XElement("Thumbnail", mediaType.Thumbnail),
                                    new XElement("Description", mediaType.Description),
                                    new XElement("AllowAtRoot", mediaType.AllowedAsRoot.ToString()));

            var masterContentType = mediaType.CompositionAliases().FirstOrDefault();
            if (masterContentType != null)
                info.Add(new XElement("Master", masterContentType));

            var structure = new XElement("Structure");
            foreach (var allowedType in mediaType.AllowedContentTypes)
            {
                structure.Add(new XElement("MediaType", allowedType.Alias));
            }

            var genericProperties = new XElement("GenericProperties");
            foreach (var propertyType in mediaType.PropertyTypes)
            {
                var definition = _dataTypeService.GetDataTypeDefinitionById(propertyType.DataTypeDefinitionId);
                var propertyGroup = mediaType.PropertyGroups.FirstOrDefault(x => x.Id == propertyType.PropertyGroupId.Value);
                var genericProperty = new XElement("GenericProperty",
                                                   new XElement("Name", propertyType.Name),
                                                   new XElement("Alias", propertyType.Alias),
                                                   new XElement("Type", propertyType.PropertyEditorAlias),
                                                   new XElement("Definition", definition.Key),
                                                   new XElement("Tab", propertyGroup == null ? "" : propertyGroup.Name),
                                                   new XElement("Mandatory", propertyType.Mandatory.ToString()),
                                                   new XElement("Validation", propertyType.ValidationRegExp),
                                                   new XElement("Description", new XCData(propertyType.Description)));
                genericProperties.Add(genericProperty);
            }

            var tabs = new XElement("Tabs");
            foreach (var propertyGroup in mediaType.PropertyGroups)
            {
                var tab = new XElement("Tab",
                                       new XElement("Id", propertyGroup.Id.ToString(CultureInfo.InvariantCulture)),
                                       new XElement("Caption", propertyGroup.Name));
                tabs.Add(tab);
            }

            var xml = new XElement("MediaType",
                                   info,
                                   structure,
                                   genericProperties,
                                   tabs);
            return xml;
        }

        #endregion

        #region Package Manifest
        #endregion

        #region Templates

        /// <summary>
        /// Imports and saves package xml as <see cref="ITemplate"/>
        /// </summary>
        /// <param name="element">Xml to import</param>
        /// <param name="userId">Optional user id</param>
        /// <param name="raiseEvents">Optional parameter indicating whether or not to raise events</param>
        /// <returns>An enumrable list of generated Templates</returns>
        public IEnumerable<ITemplate> ImportTemplates(XElement element, int userId = 0, bool raiseEvents = true)
        {
            if (raiseEvents)
            {
                if (ImportingTemplate.IsRaisedEventCancelled(new SaveEventArgs<XElement>(element), this))
                    return Enumerable.Empty<ITemplate>();
            }

            var name = element.Name.LocalName;
            if (name.Equals("Templates") == false && name.Equals("Template") == false)
            {
                throw new ArgumentException("The passed in XElement is not valid! It does not contain a root element called 'Templates' for multiple imports or 'Template' for a single import.");
            }

            var templates = new List<ITemplate>();
            var templateElements = name.Equals("Templates")
                                       ? (from doc in element.Elements("Template") select doc).ToList()
                                       : new List<XElement> { element };

            var fields = new List<TopologicalSorter.DependencyField<XElement>>();
            foreach (XElement tempElement in templateElements)
            {
                var dependencies = new List<string>();
                var elementCopy = tempElement;
                //Ensure that the Master of the current template is part of the import, otherwise we ignore this dependency as part of the dependency sorting.
                if (elementCopy.Element("Master") != null &&
                    string.IsNullOrEmpty(elementCopy.Element("Master").Value) == false &&
                    templateElements.Any(x => x.Element("Alias").Value == elementCopy.Element("Master").Value))
                {
                    dependencies.Add(elementCopy.Element("Master").Value);
                }
                else if (elementCopy.Element("Master") != null &&
                         string.IsNullOrEmpty(elementCopy.Element("Master").Value) == false &&
                         templateElements.Any(x => x.Element("Alias").Value == elementCopy.Element("Master").Value) ==
                         false)
                {
                    LogHelper.Info<PackagingService>(string.Format("Template '{0}' has an invalid Master '{1}', so the reference has been ignored.", elementCopy.Element("Alias").Value, elementCopy.Element("Master").Value));
                }

                var field = new TopologicalSorter.DependencyField<XElement>
                                {
                                    Alias = elementCopy.Element("Alias").Value,
                                    Item = new Lazy<XElement>(() => elementCopy),
                                    DependsOn = dependencies.ToArray()
                                };

                fields.Add(field);
            }
            //Sort templates by dependencies to a potential master template
            var sortedElements = TopologicalSorter.GetSortedItems(fields);
            foreach (var templateElement in sortedElements)
            {
                var templateName = templateElement.Element("Name").Value;
                var alias = templateElement.Element("Alias").Value;
                var design = templateElement.Element("Design").Value;
                var masterElement = templateElement.Element("Master");

                var isMasterPage = IsMasterPageSyntax(design);
                var path = isMasterPage ? MasterpagePath(alias) : ViewPath(alias);

                var existingTemplate = _fileService.GetTemplate(alias) as Template;
                var template = existingTemplate ?? new Template(path, templateName, alias);
                template.Content = design;
                if (masterElement != null && string.IsNullOrEmpty(masterElement.Value) == false)
                {
                    template.MasterTemplateAlias = masterElement.Value;
                    var masterTemplate = templates.FirstOrDefault(x => x.Alias == masterElement.Value);
                    if (masterTemplate != null)
                        template.MasterTemplateId = new Lazy<int>(() => masterTemplate.Id);
                }
                templates.Add(template);
            }

            if (templates.Any())
                _fileService.SaveTemplate(templates, userId);

            if(raiseEvents)
                TemplateImported.RaiseEvent(new SaveEventArgs<ITemplate>(templates, false), this);

            return templates;
        }

        private bool IsMasterPageSyntax(string code)
        {
            return Regex.IsMatch(code, @"<%@\s*Master", RegexOptions.IgnoreCase) ||
                code.InvariantContains("<umbraco:Item") || code.InvariantContains("<asp:") || code.InvariantContains("<umbraco:Macro");
        }

        private string ViewPath(string alias)
        {
            return SystemDirectories.MvcViews + "/" + alias.Replace(" ", "") + ".cshtml";
        }

        private string MasterpagePath(string alias)
        {
            return IOHelper.MapPath(SystemDirectories.Masterpages + "/" + alias.Replace(" ", "") + ".master");
        }

        #endregion

        #region Event Handlers
        /// <summary>
        /// Occurs before Importing Content
        /// </summary>
        public static event TypedEventHandler<IPackagingService, SaveEventArgs<XElement>> ImportingContent;

        /// <summary>
        /// Occurs after Content is Imported and Saved
        /// </summary>
        public static event TypedEventHandler<IPackagingService, SaveEventArgs<IContent>> ContentImported;

        /// <summary>
        /// Occurs before Exporting Content
        /// </summary>
        public static event TypedEventHandler<IPackagingService, SaveEventArgs<IContent>> ExportingContent;

        /// <summary>
        /// Occurs after Content is Exported to Xml
        /// </summary>
        public static event TypedEventHandler<IPackagingService, SaveEventArgs<XElement>> ContentExported;

        /// <summary>
        /// Occurs before Exporting Media
        /// </summary>
        public static event TypedEventHandler<IPackagingService, SaveEventArgs<IMedia>> ExportingMedia;

        /// <summary>
        /// Occurs after Media is Exported to Xml
        /// </summary>
        public static event TypedEventHandler<IPackagingService, SaveEventArgs<XElement>> MediaExported;

        /// <summary>
        /// Occurs before Importing ContentType
        /// </summary>
        public static event TypedEventHandler<IPackagingService, SaveEventArgs<XElement>> ImportingContentType;

        /// <summary>
        /// Occurs after ContentType is Imported and Saved
        /// </summary>
        public static event TypedEventHandler<IPackagingService, SaveEventArgs<IContentType>> ContentTypeImported;

        /// <summary>
        /// Occurs before Exporting ContentType
        /// </summary>
        public static event TypedEventHandler<IPackagingService, SaveEventArgs<IContentType>> ExportingContentType;

        /// <summary>
        /// Occurs after ContentType is Exported to Xml
        /// </summary>
        public static event TypedEventHandler<IPackagingService, SaveEventArgs<XElement>> ContentTypeExported;

        /// <summary>
        /// Occurs before Importing DataType
        /// </summary>
        public static event TypedEventHandler<IPackagingService, SaveEventArgs<XElement>> ImportingDataType;

        /// <summary>
        /// Occurs after DataType is Imported and Saved
        /// </summary>
        public static event TypedEventHandler<IPackagingService, SaveEventArgs<IDataTypeDefinition>> DataTypeImported;

        /// <summary>
        /// Occurs before Importing Template
        /// </summary>
        public static event TypedEventHandler<IPackagingService, SaveEventArgs<XElement>> ImportingTemplate;

        /// <summary>
        /// Occurs after Template is Imported and Saved
        /// </summary>
        public static event TypedEventHandler<IPackagingService, SaveEventArgs<ITemplate>> TemplateImported;
        #endregion

        #region Stylesheets
        #endregion
    }
}