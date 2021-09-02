using UnityEngine;
using UnityEngine.UI;

namespace Mirror.Examples.Pong
{
    public class Player : NetworkBehaviour
    {
        public float speed = 30;
        public Rigidbody2D rigidbody2d;
        [SyncVar (hook = nameof(ChangeRacketColor))]
        public Color racketColor;
        
        public void Start()
        {
            if(isLocalPlayer)
            {
                CmdChangeColor();
            }
        }

        [Command]
        private void CmdChangeColor()
        {
            racketColor = Color.red;
        }

        void ChangeRacketColor(Color _oldColor, Color _newColor)
        {
            this.transform.GetComponent<SpriteRenderer>().color = racketColor;
        }        

        // need to use FixedUpdate for rigidbody
        void FixedUpdate()
        {
            // only let the local player control the racket.
            // don't control other player's rackets
            if (isLocalPlayer)
                rigidbody2d.velocity = new Vector2(0, Input.GetAxisRaw("Vertical")) * speed * Time.fixedDeltaTime;
        }

    }
}
