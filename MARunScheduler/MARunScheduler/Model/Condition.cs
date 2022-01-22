using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.Remoting.Messaging;
using System.Text;
using System.Threading.Tasks;
using System.Xml.Serialization;

namespace Granfeldt
{
    public enum ConditionOperator
    {
        [XmlEnum(Name = "And")]
        And,
        [XmlEnum(Name = "Or")]
        Or
    }

    [Serializable]
    [
        XmlInclude(typeof(SubCondition)),
        XmlInclude(typeof(WithinMinutesSpan)),
        XmlInclude(typeof(WithinHoursSpan)),
        XmlInclude(typeof(Between))
    ]
    public class ConditionBase
    {
        public virtual string Name
        {
            get
            {
                return $"Condition {nameof(ConditionBase)}";
            }
        }
        public virtual bool IsMet(string ThreadName = null)
        {
            return true;
        }
    }

    public class Conditions
    {
        [XmlAttribute]
        public ConditionOperator Operator { get; set; }

        [XmlElement("Condition")]
        public List<ConditionBase> ConditionBase { get; set; }

        public bool AreMet()
        {
            if (ConditionBase == null || ConditionBase.Count == 0)
            {
                return true; // assume true if no conditions
            }
            if (Operator.Equals(ConditionOperator.And))
            {
                bool met = true;
                foreach (ConditionBase condition in ConditionBase)
                {
                    met = condition.IsMet();
                    this.LogMessage($"AND: {condition.Name}: {(met ? "met" : "NOT met")}");
                    if (met == false) break;
                }
                return met;
            }
            else
            {
                bool met = false;
                foreach (ConditionBase condition in ConditionBase)
                {
                    met = condition.IsMet();
                    this.LogMessage($"OR: {condition.Name}: {(met ? "met" : "NOT met")}");
                    if (met == true) break;
                }
                return met;
            }
        }

        public Conditions()
        {
            this.ConditionBase = this.ConditionBase ?? new List<ConditionBase>();
        }
    }

    public class SubCondition : ConditionBase
    {
        [XmlAttribute]
        public ConditionOperator Operator { get; set; }
        [XmlElement("Condition")]
        public List<ConditionBase> Conditions { get; set; }
        public override string Name => $"Condition {nameof(SubCondition)}";

        public SubCondition()
        {
            this.Conditions = new List<ConditionBase>();
        }

        public override bool IsMet(string ThreadName = null)
        {
            if (Operator.Equals(ConditionOperator.And))
            {
                bool met = true;
                foreach (ConditionBase condition in Conditions)
                {
                    met = condition.IsMet();
                    this.LogMessage($"AND: {this.Name}->{condition.Name}: {(met ? "met" : "NOT met")}");
                    if (met == false) break;
                }
                return met;
            }
            else
            {
                bool met = false;
                foreach (ConditionBase condition in Conditions)
                {
                    met = condition.IsMet();
                    this.LogMessage($"OR: {this.Name}->{condition.Name}: {(met ? "met" : "NOT met")}");
                    if (met == true) break;
                }
                return met;
            }
        }
    }

}
