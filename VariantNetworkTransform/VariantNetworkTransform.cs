using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using VNT;

public class VariantNetworkTransform : VariantNetworkTransformBase
{
    [Header("Target")]
    [Tooltip("Set target transform to sync. Defaults to this object if null. Script must be on parent object.")]
    public Transform target;
    protected override Transform targetComponent => target == null? this.transform : target;
    
}
