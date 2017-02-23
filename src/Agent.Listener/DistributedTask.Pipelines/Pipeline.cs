﻿using System;
using System.Collections.Generic;
using YamlDotNet.Serialization;

namespace Microsoft.TeamFoundation.DistributedTask.Orchestration.Server.Pipelines
{
    public sealed class Pipeline
    {
        [YamlMember(Alias = "resources")]
        public List<PipelineResource> Resources { get; set; }

        [YamlMember(Alias = "jobs")]
        public List<PipelineJobTemplate> Jobs { get; set; }

        [YamlMember(Alias = "template")]
        public PipelineTemplateReference Template { get; set; }
    }

    public sealed class PipelineResource
    {
        [YamlMember(Alias = "name")]
        public String Name { get; set; }

        [YamlMember(Alias = "type")]
        public String Type { get; set; }

        [YamlMember(Alias = "data")]
        public IDictionary<String, Object> Data { get; set; }
    }

    public abstract class PipelineJobStep
    {
        public String Name { get; set; }
    }

    public sealed class ImportStep : PipelineJobStep
    {
    }

    public sealed class ExportStep : PipelineJobStep
    {
        public String ResourceType { get; set; }

        public IDictionary<String, String> Inputs { get; set; }
    }

    public sealed class GroupStep : PipelineJobStep
    {
    }

    public sealed class TaskReference
    {
        public String Name { get; set; }

        public String Version { get; set; }
    }

    public sealed class TaskStep : PipelineJobStep
    {
        public TaskReference Reference { get; set; }

        public IDictionary<String, String> Inputs { get; set; }
    }

    public class PipelineJob
    {
    }

    public sealed class PipelineJobTemplate
    {
        public PipelineJobTemplate()
        {
            Steps = new List<PipelineJobStep>();
        }

        [YamlMember(Alias = "name")]
        public String Name { get; set; }

        [YamlMember(Alias = "target")]
        public IDictionary<String, String> Target { get; set; }

        [YamlMember(Alias = "variables")]
        public VariableGroupTemplate Variables { get; set; }

        [YamlMember(Alias = "steps")]
        public List<PipelineJobStep> Steps { get; set; }

        [YamlMember(Alias = "with_items")]
        public IteratorValueTemplate WithItems { get; set; }

        public IList<PipelineJob> ApplyInputs(IDictionary<String, Object> inputs)
        {
            return new List<PipelineJob>();
        }
    }

    public sealed class PipelineTemplate
    {
        [YamlMember(Alias = "resources")]
        public List<PipelineResource> Resources { get; set; }

        [YamlMember(Alias = "jobs")]
        public List<PipelineJobTemplate> Jobs { get; set; }
    }

    public sealed class PipelineTemplateReference
    {
        [YamlMember(Alias = "name")]
        public String Name { get; set; }

        [YamlMember(Alias = "parameters")]
        public IDictionary<String, Object> Parameters { get; set; }
    }

    public class TaskGroup
    {
        public IDictionary<String, String> Inputs { get; set; }

        public List<TaskStep> Tasks { get; set; }
    }

    public class VariableGroupTemplate
    {
        public VariableGroupTemplate(String templateValue)
        {
            m_templateValue = templateValue;
        }

        public VariableGroupTemplate(IDictionary<String, String> literalValue)
        {
            m_literalValue = literalValue;
        }

        public IDictionary<String, String> Resolve(PipelineTemplateContext context)
        {
            if (m_literalValue != null)
            {
                return m_literalValue;
            }
            else
            {
                return context.ResolveValue<IDictionary<String, String>>(m_templateValue);
            }
        }

        private readonly String m_templateValue;
        private readonly IDictionary<String, String> m_literalValue;
    }

    public abstract class IteratorValue
    {
        public abstract T Resolve<T>(PipelineTemplateContext context);
    }

    public sealed class IteratorValueLiteral
    {
    }

    public sealed class IteratorValueExpression
    {
    }

    public sealed class IteratorValueTemplate
    {
        public IteratorValueTemplate(String value)
        {
            m_templateValue = value;
        }

        public IteratorValueTemplate(List<String> value)
        {
            m_stringArrayLiteral = value;
        }

        public IteratorValueTemplate(List<Dictionary<String, String>> value)
        {
            m_mappingArrayLiteral = value;
        }

        public T Resolve<T>(PipelineTemplateContext context)
        {
            if (m_stringArrayLiteral != null)
            {
                return (T)(Object)m_stringArrayLiteral;
            }
            else if (m_mappingArrayLiteral != null)
            {
                return (T)(Object)m_mappingArrayLiteral;
            }
            else
            {
                return context.ResolveValue<T>(m_templateValue);
            }
        }

        private String m_templateValue;
        private List<String> m_stringArrayLiteral;
        private List<Dictionary<String, String>> m_mappingArrayLiteral;
    }

    public abstract class PipelineValue
    {
        protected PipelineValue(Object value)
        {
            this.Value = value;
        }

        public abstract PipelineValueKind Kind { get; }

        protected Object Value { get; set; }

        public virtual T Resolve<T>(PipelineTemplateContext context)
        {
            return (T)this.Value;
        }

        public static implicit operator PipelineValue(String value)
        {
            return new StringValue(value);
        }

        public static implicit operator PipelineValue(String[] value)
        {
            return new StringArrayValue(value);
        }

        public static implicit operator PipelineValue(List<String> value)
        {
            return new StringArrayValue(value);
        }

        public static implicit operator PipelineValue(Dictionary<String, String> value)
        {
            return new StringDictionaryValue(value);
        }
    }

    public class StringValue : PipelineValue
    {
        public StringValue(String value)
            : base(value)
        {
        }

        public override PipelineValueKind Kind
        {
            get
            {
                return PipelineValueKind.String;
            }
        }

        public new String Value
        {
            get
            {
                return (String)base.Value;
            }
            set
            {
                base.Value = value;
            }
        }

        public override T Resolve<T>(PipelineTemplateContext context)
        {
            return context.ResolveValue<T>(this.Value);
        }

        public static explicit operator String(StringValue val)
        {
            return val.Value;
        }
    }

    public sealed class StringArrayValue : PipelineValue
    {
        public StringArrayValue(IList<String> value)
            : base(value)
        {
        }

        public override PipelineValueKind Kind
        {
            get
            {
                return PipelineValueKind.StringArray;
            }
        }

        public new IList<String> Value
        {
            get
            {
                return (IList<String>)base.Value;
            }
            set
            {
                base.Value = value;
            }
        }
    }

    public sealed class StringDictionaryValue : PipelineValue
    {
        public StringDictionaryValue(IDictionary<String, String> value)
            : base(value)
        {
        }

        public override PipelineValueKind Kind
        {
            get
            {
                return PipelineValueKind.StringMapping;
            }
        }

        public new IDictionary<String, String> Value
        {
            get
            {
                return (IDictionary<String, String>)base.Value;
            }
            set
            {
                base.Value = value;
            }
        }
    }

    public sealed class StringDictionaryArrayValue : PipelineValue
    {
        public StringDictionaryArrayValue(IList<IDictionary<String, String>> value)
            : base(value)
        {
        }

        public override PipelineValueKind Kind
        {
            get
            {
                return PipelineValueKind.StringMappingArray;
            }
        }

        public new IList<IDictionary<String, String>> Value
        {
            get
            {
                return (IList<IDictionary<String, String>>)base.Value;
            }
            set
            {
                base.Value = value;
            }
        }
    }

    public enum PipelineValueKind
    {
        String,
        StringArray,
        StringMapping,
        StringMappingArray,
        VariableReference,
    }
}
