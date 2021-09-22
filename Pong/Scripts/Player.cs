using UnityEngine;
using Mirror;

public class Player : NetworkBehaviour
{
    public float speed = 30;
    public Rigidbody2D rigidbody2d;
    public int frameCount = 0;

    // need to use FixedUpdate for rigidbody

    void FixedUpdate()
    {
        // only let the local player control the racket.
        // don't control other player's rackets
        if (isLocalPlayer)

            rigidbody2d.velocity = new Vector2(0, Input.GetAxisRaw("Vertical")) * speed * Time.fixedDeltaTime;
            //Physics2D.Simulate(Time.fixedDeltaTime);
    }
}
